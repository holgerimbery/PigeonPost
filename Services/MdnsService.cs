// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Linq;
using Makaretu.Dns;
using PigeonPost.Models;

namespace PigeonPost.Services;

/// <summary>
/// A peer discovered on the local network via mDNS.
/// </summary>
/// <param name="Name">Machine name of the remote PC (e.g. <c>DESKTOP-ABC</c>).</param>
/// <param name="Host">Resolvable host name (e.g. <c>DESKTOP-ABC.local</c>) or IP address.</param>
/// <param name="Port">TCP port the peer's PigeonPost server listens on.</param>
public sealed record DiscoveredPeerInfo(string Name, string Host, int Port);

/// <summary>
/// Advertises the PigeonPost HTTP serveras a Bonjour/mDNS service so iOS clients
/// (PigeonPostCompanion) can auto-discover it on the local network without manual
/// IP entry.
///
/// <para>
/// Also <em>browses</em> for other PigeonPost instances on the local network so the
/// Peers feature can show them as candidates for clipboard/file push without the user
/// having to type an IP address.
/// </para>
///
/// <para>
/// Service type  : <c>_pigeonpost._tcp</c><br/>
/// Instance name : <see cref="Environment.MachineName"/> (e.g. <c>DESKTOP-ABC</c>)<br/>
/// Port          : <see cref="Constants.Port"/> (default 2560)
/// </para>
///
/// <para>
/// The iOS app will see the service as <c>DESKTOP-ABC.local</c> in the discovery
/// sheet and populate the Host field automatically when the user taps it.
/// </para>
/// </summary>
public sealed class MdnsService : IDisposable
{
    private readonly AppState _state;
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private ServiceProfile?   _profile;

    private static readonly string ServiceType = "_pigeonpost._tcp";

    // ----------------------------------------------------------------- peer discovery events

    /// <summary>
    /// Raised (from any thread) when a new <c>_pigeonpost._tcp</c> peer appears on the
    /// local network. Not raised for this machine's own advertisement.
    /// </summary>
    public event Action<DiscoveredPeerInfo>? PeerDiscovered;

    /// <summary>
    /// Raised (from any thread) when a previously advertised peer sends a goodbye packet.
    /// The argument is the peer's machine name.
    /// </summary>
    public event Action<string>? PeerLost;

    /// <param name="state">Shared application state; used for log emission.</param>
    public MdnsService(AppState state) => _state = state;

    // ----------------------------------------------------------------- start / stop

    /// <summary>
    /// Starts mDNS advertisement. Sends an initial announcement so nearby iOS devices
    /// discover the service immediately without waiting for a query.
    /// Also begins browsing for other <c>_pigeonpost._tcp</c> peers on the network.
    /// Safe to call from any thread.
    /// </summary>
    public void Start()
    {
        try
        {
            _mdns    = new MulticastService();
            _sd      = new ServiceDiscovery(_mdns);
            _profile = new ServiceProfile(
                instanceName: Environment.MachineName,
                serviceName:  ServiceType,
                port:         (ushort)Constants.Port);

            // ── Advertise our own instance ──
            _sd.Advertise(_profile);

            // ── Browse for other PigeonPost instances ──
            _sd.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
            _sd.ServiceInstanceShutdown   += OnServiceInstanceShutdown;

            _mdns.Start();

            // Trigger an immediate probe so already-running peers respond right away.
            _sd.QueryServiceInstances(ServiceType);

            _state.Emit(LogLevel.Info,
                $"mDNS: advertising {Environment.MachineName}.{ServiceType}.local:{Constants.Port}");
        }
        catch (Exception ex)
        {
            _state.Emit(LogLevel.Warn, $"mDNS advertisement failed to start: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends mDNS goodbye packets and stops the multicast service.
    /// Safe to call from any thread; idempotent.
    /// </summary>
    public void Stop()
    {
        if (_sd != null)
        {
            _sd.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
            _sd.ServiceInstanceShutdown   -= OnServiceInstanceShutdown;
        }
        try { if (_profile != null) _sd?.Unadvertise(_profile); } catch { /* best-effort goodbye */ }
        try { _mdns?.Stop(); }   catch { }
        try { _sd?.Dispose(); }  catch { }
        try { _mdns?.Dispose(); } catch { }

        _profile = null;
        _sd      = null;
        _mdns    = null;
    }

    /// <summary>Stops and restarts the advertisement (e.g. after a network change).</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    // ----------------------------------------------------------------- browse handlers

    /// <summary>
    /// Called (on an mDNS thread) when a service instance is announced on the network.
    /// Ignores this machine's own advertisement and extracts the peer's host and port
    /// from the SRV/A records in the DNS message when available.
    /// </summary>
    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        try
        {
            // The first label of the instance name is the machine name
            // (e.g. "DESKTOP-ABC" from "DESKTOP-ABC._pigeonpost._tcp.local.").
            var labels = e.ServiceInstanceName.Labels;
            if (labels == null || labels.Count == 0) return;

            var instanceName = labels[0];
            if (instanceName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return;

            // Prefer the actual IP from the A record; fall back to .local mDNS name.
            var aRecord = e.Message?.AdditionalRecords
                .OfType<ARecord>()
                .FirstOrDefault();

            // Prefer the port from the SRV record; fall back to the default port.
            var srvRecord = e.Message?.AdditionalRecords
                .OfType<SRVRecord>()
                .FirstOrDefault();

            var host = aRecord?.Address?.ToString() ?? $"{instanceName}.local";
            var port = srvRecord?.Port ?? (ushort)Constants.Port;

            var info = new DiscoveredPeerInfo(instanceName, host, port);
            PeerDiscovered?.Invoke(info);

            _state.Emit(LogLevel.Info, $"mDNS: discovered peer {instanceName} at {host}:{port}");
        }
        catch (Exception ex)
        {
            _state.Emit(LogLevel.Warn, $"mDNS: error processing peer discovery — {ex.Message}");
        }
    }

    /// <summary>
    /// Called (on an mDNS thread) when a service instance sends a goodbye packet.
    /// </summary>
    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        try
        {
            var labels = e.ServiceInstanceName.Labels;
            if (labels == null || labels.Count == 0) return;

            var instanceName = labels[0];
            if (instanceName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return;

            PeerLost?.Invoke(instanceName);
            _state.Emit(LogLevel.Info, $"mDNS: peer {instanceName} went offline");
        }
        catch { /* best-effort */ }
    }
}
