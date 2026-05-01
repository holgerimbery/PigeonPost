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