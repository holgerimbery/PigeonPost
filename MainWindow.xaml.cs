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
    private MenuFlyoutItem? _pauseMenuItem;
    private bool _isQuitting;
    private bool _sizeClamping;
    private ActivityLogWindow? _activityLogWindow;
    private SettingsWindow? _settingsWindow;

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
            // Left-click on the tray icon opens the window.
            LeftClickCommand = ViewModel.ShowWindowCommand,
        };

        // Load the icon synchronously via Win32 HICON (System.Drawing.Icon).
        // BitmapImage/ImageSource loads asynchronously and is not yet ready when
        // ForceCreate() runs, which causes the tray and taskbar to show a blank icon.
        if (File.Exists(IconPath))
        {
            _trayNativeIcon   = new System.Drawing.Icon(IconPath);
            _trayIcon.Icon    = _trayNativeIcon;
        }

        _trayIcon.ForceCreate();

        // Once the visual tree is ready, give the ContextFlyout a XamlRoot so that
        // click events route through the WinUI event system on the correct thread.
        RootGrid.Loaded += (_, _) =>
        {
            if (_trayIcon?.ContextFlyout != null)
                _trayIcon.ContextFlyout.XamlRoot = RootGrid.XamlRoot;
        };
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
