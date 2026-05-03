// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Makaretu.Dns;
using PigeonPost.Models;

namespace PigeonPost.Services;

/// <summary>
/// Advertises the PigeonPost HTTP server as a Bonjour/mDNS service so iOS clients
/// (PigeonPostCompanion) can auto-discover it on the local network without manual
/// IP entry.
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

    /// <param name="state">Shared application state; used for log emission.</param>
    public MdnsService(AppState state) => _state = state;

    // ----------------------------------------------------------------- start / stop

    /// <summary>
    /// Starts mDNS advertisement. Sends an initial announcement so nearby iOS devices
    /// discover the service immediately without waiting for a query.
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

            _sd.Advertise(_profile);
            _mdns.Start();

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
}
