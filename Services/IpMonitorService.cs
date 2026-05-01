// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Net.NetworkInformation;
using System.Threading;

namespace PigeonPost.Services;

/// <summary>
/// Monitors the primary local IPv4 address and raises <see cref="IpChanged"/> when it changes.
///
/// <para>
/// Uses <see cref="NetworkChange.NetworkAddressChanged"/> for instant detection
/// (no polling), with a 3-second debounce to avoid spurious events during network
/// transitions (e.g. Windows briefly reports no address before assigning the new one).
/// </para>
/// </summary>
public sealed class IpMonitorService : IDisposable
{
    /// <summary>
    /// Delay after a network-change event before the IP is re-sampled.
    /// Allows the OS time to complete the IP assignment before we read the new value.
    /// </summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(3);

    /// <summary>Raised on a thread-pool thread when the primary IP address changes.</summary>
    public event EventHandler<IpChangedEventArgs>? IpChanged;

    private string _lastIp;
    private Timer?  _debounce;
    private bool    _started;

    public IpMonitorService()
    {
        _lastIp = NetworkHelper.GetPrimaryLocalIp();
    }

    /// <summary>Subscribes to OS network-change events and begins monitoring.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    /// <summary>Unsubscribes from OS events and stops the debounce timer.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Called by the OS on a thread-pool thread whenever any network address changes.
    /// Resets (or starts) the debounce timer so we check the IP once things settle.
    /// </summary>
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Reset the debounce window — consecutive events extend the wait.
        _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
        _debounce = new Timer(CheckIpChange, null, DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Invoked by the debounce timer after the network has stabilised.
    /// Compares the current IP to the last known value and fires <see cref="IpChanged"/> on change.
    /// </summary>
    private void CheckIpChange(object? _)
    {
        var current  = NetworkHelper.GetPrimaryLocalIp();
        var previous = _lastIp;
        if (current == previous) return;

        _lastIp = current;
        IpChanged?.Invoke(this, new IpChangedEventArgs(previous, current));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _debounce?.Dispose();
    }
}

/// <summary>Event data for <see cref="IpMonitorService.IpChanged"/>.</summary>
public sealed class IpChangedEventArgs(string previousIp, string newIp) : EventArgs
{
    /// <summary>The IP address that was active before the change.</summary>
    public string PreviousIp { get; } = previousIp;

    /// <summary>The IP address now active.</summary>
    public string NewIp { get; } = newIp;
}
