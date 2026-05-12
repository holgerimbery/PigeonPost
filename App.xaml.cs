// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#if STORE_BUILD
using Microsoft.Windows.AppLifecycle;
#endif
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

    /// <summary>Advertises the server via Bonjour/mDNS for iOS auto-discovery.</summary>
    public static MdnsService? Mdns { get; private set; }

    /// <summary>Monitors network address changes and triggers listener restarts.</summary>
    public static IpMonitorService? IpMonitor { get; private set; }

    /// <summary>Sends clipboard text and files to remote PigeonPost peers.</summary>
    public static PeerSendService? Sender { get; private set; }

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
        // For unpackaged apps (Winget build) this uses the process AUMID; must be called
        // before any AppNotificationManager.Show() call.
        // For packaged apps (Store build) the AUMID comes from the package manifest; the
        // call is still valid and recommended to initialise the notification pipeline.
        // Failures are silently swallowed so a machine policy can't prevent app startup.
        try { AppNotificationManager.Default.Register(); }
        catch { /* notifications unavailable — continue without them */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if !STORE_BUILD
        // Keep the registry path current if the EXE was moved or updated.
        AutostartService.RefreshPathIfEnabled();

        // Detect whether Windows launched us at login (via the registry Run entry).
        var startMinimised = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
#else
        // For Store/MSIX builds the StartupTask launches the packaged EXE directly.
        // Detect this via the activation kind rather than command-line arguments.
        var activatedArgs  = AppInstance.GetCurrent().GetActivatedEventArgs();
        var startMinimised = activatedArgs?.Kind == ExtendedActivationKind.StartupTask;
#endif

        // Create and show the window first so its DispatcherQueue exists before
        // the listener is started (clipboard operations need it).
        _window   = new MainWindow(State);
        ViewModel = _window.ViewModel;

        if (startMinimised)
        {
            // Activate once (required to initialise AppWindow) then immediately hide.
            _window.Activate();
            _window.AppWindow?.Hide();
        }
        else
        {
            _window.Activate();
        }

        // Start the HTTP listener so remote clients can send files and clipboard data.
        try
        {
            Listener = new ListenerService(State, _window.DispatcherQueue);
            Listener.Start();
        }
        catch (Exception ex)
        {
            State.Emit(LogLevel.Error, $"Listener failed to start: {ex.Message}");
        }

        // Advertise the server via Bonjour/mDNS so PigeonPostCompanion can auto-discover it.
        try
        {
            Mdns = new MdnsService(State);
            Mdns.Start();
        }
        catch (Exception ex)
        {
            State.Emit(LogLevel.Error, $"mDNS failed to start: {ex.Message}");
        }

        // HTTP client for pushing clipboard / files to remote PigeonPost peers.
        Sender = new PeerSendService(State);

        // Start the IP monitor; restart the listener and notify the user on change.
        try
        {
            IpMonitor = new IpMonitorService();
            IpMonitor.NetworkChanged  += OnNetworkChanged;
            IpMonitor.TailscaleChanged += OnTailscaleChanged;
            IpMonitor.Start();
        }
        catch (Exception ex)
        {
            State.Emit(LogLevel.Error, $"IP monitor failed to start: {ex.Message}");
        }

        // Check for updates at startup, then every 24 hours while the app is running.
        // Store builds: updates are handled by the Microsoft Store — skip entirely.
#if !STORE_BUILD
        _ = CheckForUpdatesAsync();
        StartPeriodicUpdateCheck();
#endif
    }

    /// <summary>
    /// Checks GitHub Releases for a newer version and shows the update banner if found.
    /// Runs on a thread-pool thread; never throws.
    /// Only compiled for the Winget/Velopack build — Store builds skip this entirely.
    /// </summary>
#if !STORE_BUILD
    private static async Task CheckForUpdatesAsync()
    {
        var update = await UpdateService.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null) return;

        ViewModel?.NotifyUpdateAvailable(update.TargetFullRelease.Version.ToString());
    }

    /// <summary>
    /// Starts a 24-hour repeating timer that re-checks for updates while the app is running.
    /// The timer is owned by the UI thread's DispatcherQueue so it stays alive as long as
    /// the window is open without needing a separate background thread.
    /// </summary>
    private void StartPeriodicUpdateCheck()
    {
        if (_window is null) return;

        var timer = _window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromHours(24);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => _ = CheckForUpdatesAsync();
        timer.Start();
    }
#endif

    /// <summary>
    /// Called on a thread-pool thread when the network state changes.
    /// Reacts differently depending on the kind of transition:
    ///
    /// <list type="bullet">
    ///   <item><term>WentOffline</term>      <description>Warn only — server cannot bind without a routable address.</description></item>
    ///   <item><term>CameOnline</term>       <description>Restart listener on new address, notify user.</description></item>
    ///   <item><term>InterfaceSwitched</term><description>Restart on new address (WiFi ↔ LAN), notify user.</description></item>
    ///   <item><term>IpChanged</term>        <description>Restart on new address (DHCP renewal), notify user.</description></item>
    /// </list>
    /// </summary>
    private void OnNetworkChanged(object? sender, NetworkChangedEventArgs e)
    {
        switch (e.ChangeKind)
        {
            // ── No connectivity ──────────────────────────────────────────────
            case NetworkChangeKind.WentOffline:
                State.Emit(LogLevel.Error,
                    $"Network lost ({KindLabel(e.PreviousKind)} was {e.PreviousIp}) · " +
                    "Server paused — waiting for network to return.");
                ShowToast(
                    "⚠️ PigeonPost — Network Lost",
                    $"The {KindLabel(e.PreviousKind)} connection ({e.PreviousIp}) was disconnected.",
                    "The server is paused. It will restart automatically when a network returns.");
                break;

            // ── Came back online ─────────────────────────────────────────────
            case NetworkChangeKind.CameOnline:
                Listener?.Restart();
                Mdns?.Restart();
                ViewModel?.UpdateListenAddress(e.NewIp);
                State.Emit(LogLevel.Warn,
                    $"Network restored ({KindLabel(e.NewKind)} {e.NewIp}) · Server restarted.");
                ShowToast(
                    "✅ PigeonPost — Network Restored",
                    $"Connected via {KindLabel(e.NewKind)}: http://{e.NewIp}:{Constants.Port}/",
                    "The server was restarted automatically.");
                break;

            // ── WiFi ↔ Ethernet switch ───────────────────────────────────────
            case NetworkChangeKind.InterfaceSwitched:
                Listener?.Restart();
                Mdns?.Restart();
                ViewModel?.UpdateListenAddress(e.NewIp);
                State.Emit(LogLevel.Warn,
                    $"Interface switched: {KindLabel(e.PreviousKind)} ({e.PreviousIp}) → " +
                    $"{KindLabel(e.NewKind)} ({e.NewIp}) · Server restarted.");
                ShowToast(
                    $"🔄 PigeonPost — Switched to {KindLabel(e.NewKind)}",
                    $"New address: http://{e.NewIp}:{Constants.Port}/",
                    $"Was on {KindLabel(e.PreviousKind)} ({e.PreviousIp}). Server restarted.");
                break;

            // ── IP address changed (same interface) ──────────────────────────
            case NetworkChangeKind.IpChanged:
                Listener?.Restart();
                Mdns?.Restart();
                ViewModel?.UpdateListenAddress(e.NewIp);
                State.Emit(LogLevel.Warn,
                    $"{KindLabel(e.NewKind)} IP changed: {e.PreviousIp} → {e.NewIp} · " +
                    "Server restarted.");
                ShowToast(
                    $"📡 PigeonPost — {KindLabel(e.NewKind)} IP Changed",
                    $"New address: http://{e.NewIp}:{Constants.Port}/",
                    $"Previous: {e.PreviousIp}. Server restarted.");
                break;
        }
    }

    /// <summary>
    /// Called on a thread-pool thread when Tailscale connects, disconnects, or changes IP.
    /// Updates the ViewModel and shows a toast notification.
    /// </summary>
    private void OnTailscaleChanged(object? sender, TailscaleChangedEventArgs e)
    {
        ViewModel?.UpdateTailscaleState(e.NewIp);

        switch (e.Kind)
        {
            case TailscaleChangeKind.Connected:
                State.Emit(LogLevel.Info,
                    $"Tailscale connected · Remote access: http://{e.NewIp}:{Constants.Port}/");
                ShowToast(
                    "Tailscale Connected",
                    $"Remote access: http://{e.NewIp}:{Constants.Port}/",
                    "You can now reach PigeonPost from outside your home network.");
                break;

            case TailscaleChangeKind.Disconnected:
                State.Emit(LogLevel.Warn,
                    "Tailscale disconnected · Remote access unavailable.");
                ShowToast(
                    "Tailscale Disconnected",
                    "Remote access via Tailscale is no longer available.",
                    "Local network access is unaffected.");
                break;

            case TailscaleChangeKind.IpChanged:
                State.Emit(LogLevel.Info,
                    $"Tailscale IP changed: {e.PreviousIp} → {e.NewIp}");
                break;
        }
    }

    /// <summary>Returns a human-readable label for a <see cref="NetworkInterfaceKind"/>.</summary>
    private static string KindLabel(NetworkInterfaceKind kind) => kind switch
    {
        NetworkInterfaceKind.WiFi     => "Wi-Fi",
        NetworkInterfaceKind.Ethernet => "Ethernet",
        _                             => "Network",
    };

    /// <summary>
    /// Shows a Windows toast notification with up to three lines of text.
    /// Failures are silently swallowed — notification failure must never crash the app.
    /// </summary>
    private static void ShowToast(string title, string line1, string line2)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(line1)
                .AddText(line2)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch { /* notifications unavailable */ }
    }
}
