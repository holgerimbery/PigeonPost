// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PigeonPost.Services;
using PigeonPost.ViewModels;
using SDColor = System.Drawing.Color;
using WUColor = Windows.UI.Color;

namespace PigeonPost;

/// <summary>
/// Main application window: Mica backdrop, stat cards, collapsible activity log,
/// and a system-tray icon via H.NotifyIcon.
///
/// Responsibilities:
///   <list type="bullet">
///     <item>Create and own the <see cref="MainViewModel"/>.</item>
///     <item>Wire ViewModel property changes to tray-icon updates.</item>
///     <item>Generate and refresh a coloured <c>.ico</c> file at runtime for the tray.</item>
///     <item>Intercept the close button to hide instead of exiting (minimize-to-tray).</item>
///   </list>
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Temporary path for the generated tray icon.
    /// Written once on startup and overwritten on every colour change.
    /// </summary>
    private static readonly string IconPath =
        Path.Combine(Path.GetTempPath(), "pigeonpost-tray.ico");

    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Version string shown in the footer, read from the assembly at runtime so it
    /// automatically reflects whatever &lt;Version&gt; is set in the .csproj.
    /// </summary>
    public string AppVersion =>
        "v" + (System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName().Version?.ToString(3) ?? "1.0.0");

    private TaskbarIcon? _trayIcon;
    private MenuFlyoutItem? _pauseMenuItem;

    /// <summary>
    /// Incremented whenever the icon colour changes so the <see cref="BitmapImage"/>
    /// URI includes a new query-string, bypassing WinUI's image cache.
    /// </summary>
    private int _iconRevision;

    public MainWindow(AppState state)
    {
        ViewModel = new MainViewModel(state, DispatcherQueue);
        InitializeComponent();

        ViewModel.HostWindow = this;
        Title = "PigeonPost";

        // Point the window/taskbar icon at the static pigeon+envelope ICO.
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "PigeonPost.ico");
            if (File.Exists(icoPath))
                AppWindow?.SetIcon(icoPath);
        }
        catch { /* AppWindow unavailable in some hosts */ }

        // Set an initial window size that comfortably shows all elements:
        //   - 640 px wide  → above the 520 DIP AdaptiveTrigger threshold → 4-col stat cards
        // Default window size — set to match user-preferred size (physical pixels).
        try
        {
            AppWindow?.Resize(new Windows.Graphics.SizeInt32(1428, 828));
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
    /// Cancels the window-close event and hides the window instead,
    /// so the app continues running in the system tray.
    /// </summary>
    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                                    Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        sender.Hide();
    }

    // ---------------------------------------------------------------- tray icon

    /// <summary>
    /// Builds the tray icon, writes the initial <c>.ico</c> file, and registers
    /// the context-menu with Show / Pause / Quit items.
    /// </summary>
    private void InitializeTrayIcon()
    {
        WriteIconFile(ViewModel.TrayIconColor);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText    = "PigeonPost — Running",
            IconSource     = BuildIconSource(),
            ContextFlyout  = BuildTrayMenu(),
            // Left-click on the tray icon opens the window.
            LeftClickCommand = ViewModel.ShowWindowCommand,
        };

        _trayIcon.ForceCreate();
    }

    /// <summary>
    /// Creates a <see cref="BitmapImage"/> that points to the current icon file.
    /// A revision query-string forces WinUI to re-load the file after a colour change.
    /// </summary>
    private BitmapImage BuildIconSource() =>
        new(new Uri($"file:///{IconPath.Replace('\\', '/')}?v={_iconRevision}"));

    /// <summary>Builds the tray context menu: Show / Pause-Resume / Quit.</summary>
    private MenuFlyout BuildTrayMenu()
    {
        var menu = new MenuFlyout();

        var show = new MenuFlyoutItem { Text = "Show window" };
        show.Click += (_, _) => ViewModel.ShowWindowCommand.Execute(null);
        menu.Items.Add(show);

        // "Pause" / "Resume" label mirrors the main-window button text.
        _pauseMenuItem = new MenuFlyoutItem { Text = ViewModel.PauseButtonText };
        _pauseMenuItem.Click += (_, _) => ViewModel.TogglePauseCommand.Execute(null);
        menu.Items.Add(_pauseMenuItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var quit = new MenuFlyoutItem { Text = "Quit" };
        quit.Click += (_, _) =>
        {
            // Dispose the tray icon before exiting so the icon is removed from the
            // taskbar immediately and does not linger as a ghost icon.
            _trayIcon?.Dispose();
            Application.Current.Exit();
        };
        menu.Items.Add(quit);

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
                // Regenerate the .ico file and point the tray icon at the new version.
                _iconRevision++;
                WriteIconFile(ViewModel.TrayIconColor);
                _trayIcon.IconSource = BuildIconSource();
                _trayIcon.ToolTipText = ViewModel.IsPaused
                    ? "PigeonPost — Paused"
                    : "PigeonPost — Running";
                break;
        }
    }

    // ---------------------------------------------------------------- icon generation

    /// <summary>
    /// Generates a 32×32 coloured circle with a centred "P" glyph and writes it
    /// as a <c>.ico</c> file to <see cref="IconPath"/>.
    /// Called on startup and on every pause/resume to reflect the current status colour.
    /// </summary>
    private static void WriteIconFile(WUColor color)
    {
        var bg = SDColor.FromArgb(color.A, color.R, color.G, color.B);

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode      = SmoothingMode.AntiAlias;
            g.TextRenderingHint  = TextRenderingHint.AntiAliasGridFit;

            // Fill the full 32x32 area so the circle touches every edge of the bitmap.
            using var bgBrush = new SolidBrush(bg);
            g.FillEllipse(bgBrush, 0, 0, 32, 32);

            // Draw a centred "P" glyph in white over the coloured circle.
            using var font  = new Font("Segoe UI", 16, System.Drawing.FontStyle.Bold,
                                       GraphicsUnit.Pixel);
            var size        = g.MeasureString("P", font);
            g.DrawString("P", font, System.Drawing.Brushes.White,
                         (32 - size.Width)  / 2f,
                         (32 - size.Height) / 2f);
        }

        // Convert the GDI Bitmap to an HICON handle, then save as .ico.
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            using var fs   = File.Create(IconPath);
            icon.Save(fs);
        }
        finally
        {
            // Always release the HICON handle to avoid a GDI handle leak.
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}