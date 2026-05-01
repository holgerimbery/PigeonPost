// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Net.NetworkInformation;
using System.Threading;

namespace PigeonPost.Services;

// ── Change-kind classification ────────────────────────────────────────────────

/// <summary>
/// Classifies what kind of network transition has occurred so the application
/// can react differently to each scenario.
/// </summary>
public enum NetworkChangeKind
{
    /// <summary>
    /// Had connectivity (WiFi or Ethernet); now offline.
    /// The server cannot be restarted — only warn the user.
    /// </summary>
    WentOffline,

    /// <summary>
    /// Was offline; now has connectivity again.
    /// Restart the listener and notify the user.
    /// </summary>
    CameOnline,

    /// <summary>
    /// The primary interface type changed (e.g. WiFi ↔ Ethernet).
    /// Restart the listener on the new IP and notify the user.
    /// </summary>
    InterfaceSwitched,

    /// <summary>
    /// Same interface type but the IP address changed (e.g. DHCP renewed).
    /// Restart the listener on the new IP and notify the user.
    /// </summary>
    IpChanged,
}

// ── Event args ────────────────────────────────────────────────────────────────

/// <summary>
/// Full context of a network-state change delivered by <see cref="IpMonitorService"/>.
/// </summary>
public sealed class NetworkChangedEventArgs : EventArgs
{
    /// <param name="previousIp">IP that was active before the change.</param>
    /// <param name="newIp">IP that is active after the change.</param>
    /// <param name="previousKind">Interface type before the change.</param>
    /// <param name="newKind">Interface type after the change.</param>
    /// <param name="changeKind">Classification of what changed.</param>
    public NetworkChangedEventArgs(
        string previousIp, string newIp,
        NetworkInterfaceKind previousKind, NetworkInterfaceKind newKind,
        NetworkChangeKind changeKind)
    {
        PreviousIp   = previousIp;
        NewIp        = newIp;
        PreviousKind = previousKind;
        NewKind      = newKind;
        ChangeKind   = changeKind;
    }

    /// <summary>IP address that was active before the transition.</summary>
    public string PreviousIp { get; }

    /// <summary>IP address that is active after the transition (loopback when offline).</summary>
    public string NewIp { get; }

    /// <summary>Interface kind before the transition.</summary>
    public NetworkInterfaceKind PreviousKind { get; }

    /// <summary>Interface kind after the transition.</summary>
    public NetworkInterfaceKind NewKind { get; }

    /// <summary>Describes what kind of transition occurred.</summary>
    public NetworkChangeKind ChangeKind { get; }
}

// ── Monitor ───────────────────────────────────────────────────────────────────

/// <summary>
/// Watches the OS network stack for address changes and classifies each
/// transition into one of four scenarios:
///
/// <list type="bullet">
///   <item><term>WentOffline</term>     <description>Had connectivity → no connectivity.</description></item>
///   <item><term>CameOnline</term>      <description>No connectivity → has connectivity.</description></item>
///   <item><term>InterfaceSwitched</term><description>WiFi ↔ Ethernet (or other) switch.</description></item>
///   <item><term>IpChanged</term>       <description>Same interface type, different IP (e.g. DHCP).</description></item>
/// </list>
///
/// Uses <see cref="NetworkChange.NetworkAddressChanged"/> for instant, event-driven
/// detection plus a 3-second debounce to wait out the transient "no address" state
/// Windows exhibits during interface switches.
/// </summary>
public sealed class IpMonitorService : IDisposable
{
    /// <summary>
    /// Time to wait after the last network-change event before sampling the new state.
    /// Chosen to outlast Windows' typical re-assignment delay during interface switches.
    /// </summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(3);

    /// <summary>Raised on a thread-pool thread when a meaningful network transition is detected.</summary>
    public event EventHandler<NetworkChangedEventArgs>? NetworkChanged;

    private string               _lastIp;
    private NetworkInterfaceKind _lastKind;
    private Timer?               _debounce;
    private bool                 _started;

    public IpMonitorService()
    {
        // Snapshot the current state so the first comparison has a baseline.
        (_lastIp, _lastKind) = NetworkHelper.GetNetworkState();
    }

    /// <summary>Subscribes to OS network-change events and begins monitoring.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    /// <summary>Unsubscribes from OS events and cancels any pending debounce timer.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _debounce?.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Invoked by the OS on a thread-pool thread for every address change on any interface.
    /// Resets the debounce window so rapid consecutive events coalesce into a single check.
    /// </summary>
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
        _debounce = new Timer(CheckNetworkChange, null, DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Invoked by the debounce timer after the network has settled.
    /// Compares current state to the last-known state and fires
    /// <see cref="NetworkChanged"/> when a meaningful change is detected.
    /// </summary>
    private void CheckNetworkChange(object? _)
    {
        var (currentIp, currentKind) = NetworkHelper.GetNetworkState();
        var previousIp   = _lastIp;
        var previousKind = _lastKind;

        // No change — nothing to do.
        if (currentIp == previousIp && currentKind == previousKind) return;

        // Persist new baseline before raising the event (prevents double-firing).
        _lastIp   = currentIp;
        _lastKind = currentKind;

        var changeKind = Classify(previousKind, currentKind, previousIp, currentIp);
        NetworkChanged?.Invoke(this, new NetworkChangedEventArgs(
            previousIp, currentIp, previousKind, currentKind, changeKind));
    }

    /// <summary>
    /// Determines the <see cref="NetworkChangeKind"/> for a given state transition.
    /// </summary>
    private static NetworkChangeKind Classify(
        NetworkInterfaceKind previousKind, NetworkInterfaceKind currentKind,
        string previousIp,                string currentIp)
    {
        // Transitioning into or out of "offline" takes priority.
        if (currentKind  == NetworkInterfaceKind.None) return NetworkChangeKind.WentOffline;
        if (previousKind == NetworkInterfaceKind.None) return NetworkChangeKind.CameOnline;

        // Both endpoints are online — check whether the interface type changed.
        if (previousKind != currentKind) return NetworkChangeKind.InterfaceSwitched;

        // Same type, different address.
        return NetworkChangeKind.IpChanged;
    }
}
