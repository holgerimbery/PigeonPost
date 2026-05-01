// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.ComponentModel;
using System.IO;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PigeonPost.Services;
using PigeonPost.ViewModels;

namespace PigeonPost;

/// <summary>
/// Main application window: Mica backdrop, stat cards, collapsible activity log,
/// and a system-tray icon via H.NotifyIcon.
///
/// Responsibilities:
///   <list type="bullet">
///     <item>Create and own the <see cref="MainViewModel"/>.</item>
///     <item>Wire ViewModel property changes to tray-icon tooltip updates.</item>
///     <item>Intercept the close button to hide instead of exiting (minimize-to-tray).</item>
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
    private MenuFlyoutItem? _pauseMenuItem;

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
    /// Builds the tray icon from the static <c>PigeonPost.ico</c> and registers
    /// the context-menu with Show / Pause / Quit items.
    /// </summary>
    private void InitializeTrayIcon()
    {
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
    /// Creates a <see cref="BitmapImage"/> pointing to the static app icon.
    /// </summary>
    private static BitmapImage BuildIconSource() =>
        new(new Uri($"file:///{IconPath.Replace('\\', '/')}"));

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
                // Update the tooltip to reflect running / paused state.
                _trayIcon.ToolTipText = ViewModel.IsPaused
                    ? "PigeonPost — Paused"
                    : "PigeonPost — Running";
                break;
        }
    }

}