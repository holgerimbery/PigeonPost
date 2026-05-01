// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using PigeonPost.Models;
using PigeonPost.Services;
using PigeonPost.ViewModels;

namespace PigeonPost;

/// <summary>
/// Application entry-point. Bootstraps shared services and launches the main window.
///
/// Startup sequence:
///   1. Create <see cref="AppState"/> (shared across all components).
///   2. Register Windows toast notifications (AppNotificationManager).
///   3. Open <see cref="MainWindow"/> (which creates the <see cref="MainViewModel"/>).
///   4. Start <see cref="ListenerService"/> using the window's DispatcherQueue so
///      clipboard operations can be marshalled to the UI thread.
///   5. Start <see cref="IpMonitorService"/> to detect network changes and restart
///      the listener automatically.
/// </summary>
public partial class App : Application
{
    /// <summary>Process-wide shared state; accessible from any component via the static property.</summary>
    public static AppState State { get; } = new();

    /// <summary>Root ViewModel; exposed for diagnostics / future extensibility.</summary>
    public static MainViewModel? ViewModel { get; private set; }

    /// <summary>The active HTTP listener; exposed so it can be stopped on shutdown if needed.</summary>
    public static ListenerService? Listener { get; private set; }

    /// <summary>Monitors network address changes and triggers listener restarts.</summary>
    public static IpMonitorService? IpMonitor { get; private set; }

    private MainWindow? _window;

    public App()
    {
        // Catch any unhandled exception (including XAML parse errors thrown after
        // InitializeComponent) and write them to a temp file before the process dies.
        this.UnhandledException += (_, e) =>
        {
            try
            {
                var log = Path.Combine(Path.GetTempPath(), "pigeonpost-crash.txt");
                File.WriteAllText(log, $"{DateTimeOffset.Now}\n\n{e.Exception}");
            }
            catch { /* logging must never throw */ }
            e.Handled = false; // let the default handler terminate the app
        };

        InitializeComponent();

        // Register for Windows toast notifications.
        // For unpackaged apps this uses the process AUMID; must be called before any
        // AppNotificationManager.Show() call. Failures are silently swallowed so a
        // machine policy or consent screen can't prevent the app from starting.
        try { AppNotificationManager.Default.Register(); }
        catch { /* notifications unavailable — continue without them */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Create and show the window first so its DispatcherQueue exists before
        // the listener is started (clipboard operations need it).
        _window   = new MainWindow(State);
        ViewModel = _window.ViewModel;
        _window.Activate();

        // Start the HTTP listener so remote clients can send files and clipboard data.
        Listener = new ListenerService(State, _window.DispatcherQueue);
        Listener.Start();

        // Start the IP monitor; restart the listener and notify the user on change.
        IpMonitor = new IpMonitorService();
        IpMonitor.IpChanged += OnIpChanged;
        IpMonitor.Start();
    }

    /// <summary>
    /// Called on a thread-pool thread when the primary IP address changes.
    /// Restarts the HTTP listener so it binds to the new address, updates the UI,
    /// logs the event, and shows a Windows toast notification.
    /// </summary>
    private void OnIpChanged(object? sender, IpChangedEventArgs e)
    {
        // Restart the listener so it binds to the new interface/address.
        Listener?.Restart();

        // Update the address card in the UI.
        ViewModel?.UpdateListenAddress(e.NewIp);

        // Log the change so the user can see it in the activity log.
        State.Emit(LogLevel.Warn,
            $"IP address changed: {e.PreviousIp} → {e.NewIp} · Server restarted automatically");

        // Show a Windows toast notification (works even when the window is minimised to tray).
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("PigeonPost — Network Change Detected")
                .AddText($"New address: http://{e.NewIp}:{Constants.Port}/")
                .AddText("The server was restarted automatically.")
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch { /* notification failure must never crash the app */ }
    }
}
