// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using Microsoft.UI.Xaml;
using PigeonPost.Services;
using PigeonPost.ViewModels;

namespace PigeonPost;

/// <summary>
/// Application entry-point. Bootstraps shared services and launches the main window.
///
/// Startup sequence:
///   1. Create <see cref="AppState"/> (shared across all components).
///   2. Open <see cref="MainWindow"/> (which creates the <see cref="MainViewModel"/>).
///   3. Start <see cref="ListenerService"/> using the window's DispatcherQueue so
///      clipboard operations can be marshalled to the UI thread.
/// </summary>
public partial class App : Application
{
    /// <summary>Process-wide shared state; accessible from any component via the static property.</summary>
    public static AppState State { get; } = new();

    /// <summary>Root ViewModel; exposed for diagnostics / future extensibility.</summary>
    public static MainViewModel? ViewModel { get; private set; }

    /// <summary>The active HTTP listener; exposed so it can be stopped on shutdown if needed.</summary>
    public static ListenerService? Listener { get; private set; }

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
    }
}