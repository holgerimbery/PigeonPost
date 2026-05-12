// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PigeonPost.Services;
using PigeonPost.ViewModels;

namespace PigeonPost;

/// <summary>
/// Main application window: Mica backdrop, stat cards,
/// and a system-tray icon via H.NotifyIcon.
///
/// Responsibilities:
///   <list type="bullet">
///     <item>Create and own the <see cref="MainViewModel"/>.</item>
///     <item>Wire ViewModel property changes to tray-icon tooltip updates.</item>
///     <item>Intercept the close button to hide instead of exiting (minimize-to-tray).</item>
///     <item>Manage single-instance <see cref="ActivityLogWindow"/> and per-open <see cref="SettingsWindow"/>.</item>
///   </list>
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Path to the static app icon copied next to the exe.</summary>
    private static readonly string IconPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "PigeonPost.ico");

    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Version string shown in the footer, read from the assembly at runtime so it
    /// automatically reflects whatever &lt;Version&gt; is set in the .csproj.
    /// </summary>
    public string AppVersion =>
        "v" + (System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName().Version?.ToString(3) ?? "1.0.0");

    private TaskbarIcon? _trayIcon;
    private System.Drawing.Icon? _trayNativeIcon;   // kept alive for the process lifetime
    private IntPtr _trayIconHandle = IntPtr.Zero;   // raw HICON — must be destroyed on quit
    // Separately-sized HICONs for the window taskbar button (owned, destroyed on quit).
    // AppWindow.SetIcon only sets ICON_BIG; we also need ICON_SMALL and class icons.
    private IntPtr _hIconBig   = IntPtr.Zero;
    private IntPtr _hIconSmall = IntPtr.Zero;
    private bool   _windowIconApplied;
    private MenuFlyoutItem? _pauseMenuItem;
    private bool _isQuitting;
    private bool _sizeClamping;
    private ActivityLogWindow? _activityLogWindow;
    private SettingsWindow?    _settingsWindow;
    private PeersWindow?       _peersWindow;

    // Maximum window dimensions in logical device-independent pixels.
    // Converted to physical pixels at runtime by multiplying with the DPI scale factor.
    private const int MaxWindowWidth  = 1000;
    private const int MaxWindowHeight = 600;

    public MainWindow(AppState state)
    {
        ViewModel = new MainViewModel(state, DispatcherQueue);
        InitializeComponent();

        ViewModel.HostWindow = this;
        Title = "PigeonPost";

        // Apply the saved theme before anything else is rendered.
        ApplyTheme(SettingsService.Current.Theme);

        // Point the window/taskbar icon at the static pigeon+envelope ICO.
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "PigeonPost.ico");
            if (File.Exists(icoPath))
                AppWindow?.SetIcon(icoPath);
        }
        catch { /* AppWindow unavailable in some hosts */ }

        // Size the window to show all content comfortably and cap it at MaxWindowWidth × MaxWindowHeight.
        // 800 logical px wide keeps the 4-column stat-card layout (AdaptiveTrigger fires at 520 DIP)
        // and leaves room for the Tailscale row without crowding.
        // AppWindow.Resize() uses physical pixels, so we multiply by the DPI scale factor so the
        // window appears the same logical size regardless of the display scaling setting.
        try
        {
            var scale = GetScaleFactor();
            AppWindow?.Resize(new Windows.Graphics.SizeInt32(
                (int)(800 * scale), (int)(540 * scale)));

            // Disable the maximize button so the user cannot accidentally blast the window
            // beyond the intended compact size.
            if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
                op.IsMaximizable = false;

            // Enforce the hard cap on every resize (e.g. when the user drags the border).
            if (AppWindow != null)
                AppWindow.Changed += OnAppWindowChanged;
        }
        catch { /* AppWindow may be unavailable on first launch in some hosts */ }

        // Intercept the title-bar close button to hide rather than terminate.
        if (AppWindow != null)
            AppWindow.Closing += OnAppWindowClosing;

        InitializeTrayIcon();

        // Re-apply window icons after the window is activated (= after Activate() is called
        // in App.OnLaunched). The taskbar button is created at that point and Windows may
        // initialise it from the window-class icon rather than the per-window icon we set
        // in the constructor. We use a one-shot handler so we don't fire on every re-activation.
        Activated += OnWindowFirstActivated;

        // Apply the initial theme-appropriate status colours now that XAML resources are loaded.
        ViewModel.RefreshStatusColors();

        // Re-apply status colours whenever the user switches Windows dark/light mode.
        RootGrid.ActualThemeChanged += (_, _) => ViewModel.RefreshStatusColors();

        // Keep the tray icon and tooltip in sync when the ViewModel changes.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // ---------------------------------------------------------------- close / hide

    /// <summary>
    /// Called before a programmatic quit so the closing handler knows not to
    /// cancel the event (which would just hide the window again).
    /// </summary>
    public void SetQuitting() => _isQuitting = true;

    /// <summary>
    /// Cancels the window-close event and hides the window instead,
    /// so the app continues running in the system tray.
    /// </summary>
    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                                    Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isQuitting) return;   // real quit — let the close proceed
        args.Cancel = true;
        sender.Hide();
    }

    /// <summary>
    /// Clamps the window to <see cref="MaxWindowWidth"/> × <see cref="MaxWindowHeight"/>
    /// (logical pixels, converted to physical pixels at runtime) whenever the user resizes
    /// it beyond those limits.
    /// A guard flag prevents re-entrancy when <c>Resize()</c> itself triggers another
    /// <c>Changed</c> event.
    /// </summary>
    private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender,
                                    Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        // Re-apply icons when the window becomes visible (e.g. restored from tray).
        if (args.DidVisibilityChange && sender.IsVisible)
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyWindowIcons);

        if (!args.DidSizeChange || _sizeClamping) return;

        var scale = GetScaleFactor();
        var maxW  = (int)(MaxWindowWidth  * scale);
        var maxH  = (int)(MaxWindowHeight * scale);

        var s    = sender.Size;
        var newW = Math.Min(s.Width,  maxW);
        var newH = Math.Min(s.Height, maxH);

        if (newW == s.Width && newH == s.Height) return;

        _sizeClamping = true;
        try   { sender.Resize(new Windows.Graphics.SizeInt32(newW, newH)); }
        finally { _sizeClamping = false; }
    }

    /// <summary>
    /// Returns the DPI scale factor for this window (e.g. 1.0 at 96 DPI, 1.5 at 144 DPI).
    /// Used to convert between logical device-independent pixels and the physical pixels that
    /// <see cref="Microsoft.UI.Windowing.AppWindow.Resize"/> requires.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // WM_SETICON — sets the per-window title-bar / Alt-Tab icon (ICON_BIG) and the
    // hint that feeds ICON_SMALL2 which the taskbar button actually uses (ICON_SMALL).
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // SetClassLongPtr — updates the window-CLASS icon so the taskbar button (ICON_SMALL2)
    // picks up the pigeon. WM_SETICON(ICON_SMALL) alone is not always sufficient because
    // Windows Vista+ derives the taskbar-button icon from GCLP_HICONSM.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // GetSystemMetrics — returns the canonical pixel size for small and large icons at the
    // current DPI so we load the ICO at the right resolution.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const uint IMAGE_ICON      = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint WM_SETICON      = 0x0080;
    private const int  ICON_SMALL      = 0;   // per-window small icon
    private const int  ICON_BIG        = 1;   // per-window large icon
    private const int  GCLP_HICON      = -14; // class large icon  — used by Alt+Tab
    private const int  GCLP_HICONSM    = -34; // class small icon  — used by taskbar button
    private const int  SM_CXICON       = 11;  // system large-icon width  (typically 32 px)
    private const int  SM_CYICON       = 12;  // system large-icon height (typically 32 px)
    private const int  SM_CXSMICON     = 49;  // system small-icon width  (typically 16–20 px)
    private const int  SM_CYSMICON     = 50;  // system small-icon height (typically 16–20 px)

    private double GetScaleFactor()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            return GetDpiForWindow(hwnd) / 96.0;
        }
        catch { return 1.0; }
    }

    // ---------------------------------------------------------------- tray icon

    /// <summary>
    /// Builds the tray icon from the static <c>PigeonPost.ico</c> and registers
    /// the context-menu with Show / Pause / Quit items.
    /// </summary>
    private void InitializeTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText      = "PigeonPost — Running",
            ContextFlyout    = BuildTrayMenu(),
            LeftClickCommand = ViewModel.ShowWindowCommand,
        };

        _trayIcon.ForceCreate();

        // ── Tray (notification-area) icon ──────────────────────────────────────
        // Primary: extract from the running EXE's embedded resources (always present
        // via <ApplicationIcon> in the csproj). This returns a 32×32 System.Drawing.Icon
        // which is sufficient for the notification area.
        // Fallback: LoadImage from the Assets file (handles PNG-compressed ICO that
        // System.Drawing.Icon constructor cannot decode).
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath != null)
                _trayNativeIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch { }

        if (_trayNativeIcon == null && File.Exists(IconPath))
        {
            var hicon = LoadImage(IntPtr.Zero, IconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
            if (hicon != IntPtr.Zero)
            {
                _trayIconHandle = hicon;
                _trayNativeIcon = System.Drawing.Icon.FromHandle(hicon);
            }
        }

        if (_trayNativeIcon != null)
            _trayIcon.Icon = _trayNativeIcon;

        // ── Window / taskbar-button icons ──────────────────────────────────────
        // Load the ICO at the exact sizes Windows uses for large and small icons so
        // we don't hand a scaled-down 32×32 bitmap to the taskbar button.
        // We load into separate HICONs (not shared with the tray icon) so ownership
        // is unambiguous — these are destroyed in the Quit handler.
        if (File.Exists(IconPath))
        {
            int bigW = GetSystemMetrics(SM_CXICON),   bigH = GetSystemMetrics(SM_CYICON);
            int smW  = GetSystemMetrics(SM_CXSMICON), smH  = GetSystemMetrics(SM_CYSMICON);
            _hIconBig   = LoadImage(IntPtr.Zero, IconPath, IMAGE_ICON, bigW, bigH, LR_LOADFROMFILE);
            _hIconSmall = LoadImage(IntPtr.Zero, IconPath, IMAGE_ICON, smW,  smH,  LR_LOADFROMFILE);
        }

        // Diagnostic log — check %TEMP%\pigeonpost-debug.txt after install.
        try
        {
            var dbg = Path.Combine(Path.GetTempPath(), "pigeonpost-debug.txt");
            File.WriteAllText(dbg,
                $"{DateTimeOffset.Now}  tray={((_trayNativeIcon != null) ? "OK" : "NULL")}  " +
                $"hBig=0x{_hIconBig:X}  hSmall=0x{_hIconSmall:X}  " +
                $"iconPath={IconPath}  exists={File.Exists(IconPath)}");
        }
        catch { }

        RootGrid.Loaded += (_, _) =>
        {
            if (_trayIcon?.ContextFlyout != null)
                _trayIcon.ContextFlyout.XamlRoot = RootGrid.XamlRoot;
        };
    }

    /// <summary>
    /// One-shot handler: applies window and class icons the first time the window is
    /// activated (i.e., after <c>window.Activate()</c> in App.OnLaunched).
    /// The taskbar button is created at this point; setting icons here ensures the
    /// button shows the pigeon icon rather than the WinUI default.
    /// </summary>
    private void OnWindowFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_windowIconApplied) return;
        _windowIconApplied = true;
        Activated -= OnWindowFirstActivated;
        // Use Low priority so the dispatcher yields first, giving Explorer time to
        // create the taskbar button before we stamp the class icon onto it.
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyWindowIcons);
    }

    /// <summary>
    /// Sets the per-window (WM_SETICON) and window-class (SetClassLongPtr) icons so
    /// that the title bar, Alt+Tab switcher, and taskbar button all show the pigeon.
    ///
    /// <para>
    /// Why three layers?
    /// <list type="bullet">
    ///   <item><term>WM_SETICON ICON_BIG</term>   <description>Title bar and Alt+Tab thumbnail.</description></item>
    ///   <item><term>WM_SETICON ICON_SMALL</term>  <description>Hints to the taskbar, but Windows Vista+
    ///     may ignore it in favour of ICON_SMALL2 which is derived from the class icon.</description></item>
    ///   <item><term>GCLP_HICONSM (class icon)</term> <description>What the Windows Vista+ taskbar
    ///     button actually uses when querying ICON_SMALL2.</description></item>
    ///   <item><term>GCLP_HICON   (class icon)</term> <description>Belt-and-suspenders for large class icon.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    private void ApplyWindowIcons()
    {
        if (_hIconBig == IntPtr.Zero && _hIconSmall == IntPtr.Zero) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            // Fall back to each other if one size failed to load.
            var hBig   = _hIconBig   != IntPtr.Zero ? _hIconBig   : _hIconSmall;
            var hSmall = _hIconSmall != IntPtr.Zero ? _hIconSmall : _hIconBig;

            // Per-window instance icons.
            SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG,   hBig);
            SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL,  hSmall);

            // Window-CLASS icons — the taskbar button uses GCLP_HICONSM (ICON_SMALL2).
            SetClassLongPtr(hwnd, GCLP_HICON,   hBig);
            SetClassLongPtr(hwnd, GCLP_HICONSM, hSmall);

            // Append result to the diagnostic log.
            try
            {
                var dbg = Path.Combine(Path.GetTempPath(), "pigeonpost-debug.txt");
                File.AppendAllText(dbg,
                    $"\nApplyWindowIcons  hwnd=0x{hwnd:X}  hBig=0x{hBig:X}  hSmall=0x{hSmall:X}");
            }
            catch { }
        }
        catch { }
    }

    /// <summary>Builds the tray context menu: Show / Pause-Resume / Quit.</summary>
    /// <remarks>
    /// Uses <c>Command</c> instead of <c>Click</c> — H.NotifyIcon invokes ICommand
    /// bindings reliably regardless of the internal window/thread it uses for its flyout,
    /// whereas <c>Click</c> event handlers can silently fail to fire.
    /// </remarks>
    private MenuFlyout BuildTrayMenu()
    {
        var menu = new MenuFlyout();

        // Show window — marshal to UI thread via DispatcherQueue as an extra safety net.
        menu.Items.Add(new MenuFlyoutItem
        {
            Text    = "Show window",
            Command = new RelayCommand(() =>
                DispatcherQueue.TryEnqueue(() => ViewModel.ShowWindowCommand.Execute(null))),
        });

        // "Pause" / "Resume" label mirrors the main-window button text.
        // TogglePause self-dispatches to the UI thread internally, so no
        // additional DispatcherQueue.TryEnqueue wrapper is needed here.
        _pauseMenuItem = new MenuFlyoutItem
        {
            Text    = ViewModel.PauseButtonText,
            Command = new RelayCommand(() => ViewModel.TogglePauseCommand.Execute(null)),
        };
        menu.Items.Add(_pauseMenuItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Quit — dispose the tray icon first, then call Environment.Exit(0).
        // Environment.Exit is used instead of Application.Current.Exit() because
        // the latter can be blocked or silently no-op in unpackaged WinUI 3 apps
        // when background threads are still running.
        menu.Items.Add(new MenuFlyoutItem
        {
            Text    = "Quit",
            Command = new RelayCommand(() =>
            {
                _trayIcon?.Dispose();
                _trayNativeIcon?.Dispose();
                if (_trayIconHandle != IntPtr.Zero) { DestroyIcon(_trayIconHandle); _trayIconHandle = IntPtr.Zero; }
                if (_hIconBig   != IntPtr.Zero) { DestroyIcon(_hIconBig);   _hIconBig   = IntPtr.Zero; }
                if (_hIconSmall != IntPtr.Zero) { DestroyIcon(_hIconSmall); _hIconSmall = IntPtr.Zero; }
                Environment.Exit(0);
            }),
        });

        return menu;
    }

    // ---------------------------------------------------------------- ViewModel → tray sync

    /// <summary>
    /// Reacts to ViewModel property changes that require tray-icon updates.
    /// Runs on the UI thread because it is subscribed via <c>PropertyChanged</c>.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_trayIcon == null) return;

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.PauseButtonText):
                // Keep the tray menu label in sync with the window's pause button.
                if (_pauseMenuItem != null)
                    _pauseMenuItem.Text = ViewModel.PauseButtonText;
                break;

            case nameof(MainViewModel.TrayIconColor):
                // Update the tooltip to reflect running / paused state.
                _trayIcon.ToolTipText = ViewModel.IsPaused
                    ? "PigeonPost — Paused"
                    : "PigeonPost — Running";
                break;
        }
    }

    // ---------------------------------------------------------------- theme

    /// <summary>
    /// Applies the requested theme to the root grid.
    /// Also propagates to the activity-log and settings windows if they are open,
    /// so all windows stay in sync.
    /// "System" (default) follows the Windows dark/light setting automatically.
    /// </summary>
    public void ApplyTheme(string theme)
    {
        var et = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark"  => ElementTheme.Dark,
            _       => ElementTheme.Default,
        };
        RootGrid.RequestedTheme = et;
        _activityLogWindow?.ApplyTheme(et);
        // SettingsWindow applies its own theme in ThemeRadios_SelectionChanged;
        // no need to push it back here to avoid loops.
    }

    // ---------------------------------------------------------------- help

    /// <summary>
    /// Opens the PigeonPost GitHub repository in the system default browser.
    /// </summary>
    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "https://github.com/holgerimbery/PigeonPost",
                UseShellExecute = true,
            });
        }
        catch { /* browser unavailable — silently ignore */ }
    }

    // ---------------------------------------------------------------- activity log

    /// <summary>
    /// Opens (or re-activates) the standalone activity-log window.
    /// A single instance is created lazily and kept alive for the session.
    /// </summary>
    private void ActivityLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activityLogWindow == null)
        {
            _activityLogWindow = new ActivityLogWindow(ViewModel);
            // Keep the activity-log window in sync with the current theme.
            _activityLogWindow.ApplyTheme(RootGrid.RequestedTheme);
        }

        _activityLogWindow.AppWindow?.Show();
        _activityLogWindow.Activate();
    }

    // ---------------------------------------------------------------- peers

    /// <summary>
    /// Opens (or re-activates) the Peers window.
    /// A single instance is kept alive for the session.
    /// </summary>
    private void PeersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_peersWindow == null)
        {
            _peersWindow = new PeersWindow(App.State, App.Mdns!, App.Sender!);
            _peersWindow.ApplyTheme(RootGrid.RequestedTheme);
        }

        _peersWindow.AppWindow?.Show();
        _peersWindow.Activate();
        // Start (or resume) mDNS peer browsing now that the window is visible.
        _peersWindow.ViewModel.StartBrowsing();
    }

    // ---------------------------------------------------------------- settings

    /// <summary>
    /// Opens the standalone settings window. Creates a fresh instance each time
    /// (mirrors the old ContentDialog lifecycle) so controls are always pre-populated
    /// from the current saved state.
    /// </summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Close any previously open settings window before opening a new one.
        _settingsWindow?.Close();

        _settingsWindow = new SettingsWindow(theme => ApplyTheme(theme));
        _settingsWindow.SettingsSaved += (_, _) => ViewModel.RefreshDownloadsLine();

        // Apply the current theme so the window matches immediately.
        _settingsWindow.ApplyTheme(RootGrid.RequestedTheme);

        _settingsWindow.Activate();
    }
}
