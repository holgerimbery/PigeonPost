// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using PigeonPost.Models;

namespace PigeonPost.Services;

/// <summary>
/// Periodically sends a <c>keepawake: ping</c> HTTP request to every saved peer that
/// has <see cref="PeerEntry.KeepAlive"/> set to <c>true</c>.
///
/// <para>
/// The ping interval (<see cref="PingIntervalSeconds"/>, default 30 s) is well within
/// the remote <see cref="KeepAwakeService.WatchdogSeconds"/> grace period (90 s), so
/// a single missed ping does not prematurely re-enable sleep on the remote machine.
/// </para>
///
/// <para>
/// Peer list and <c>KeepAlive</c> flags are read from
/// <see cref="SettingsService.Current"/> on every tick so changes take effect
/// within one interval without requiring a service restart.
/// </para>
/// </summary>
public sealed class PeerKeepAliveService : IDisposable
{
    /// <summary>Seconds between keep-alive pings to each enabled peer.</summary>
    public const int PingIntervalSeconds = 30;

    private readonly AppState        _state;
    private readonly PeerSendService _sender;
    private CancellationTokenSource? _cts;

    public PeerKeepAliveService(AppState state, PeerSendService sender)
    {
        _state  = state;
        _sender = sender;
    }

    // ---- Lifecycle ------------------------------------------------------------

    /// <summary>Starts the periodic ping loop on a background thread.</summary>
    public void Start()
    {
        if (_cts != null) return;   // already running
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Stops the ping loop and cancels any in-flight requests.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    // ---- Background loop ------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(PingIntervalSeconds));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var peers = SettingsService.Current.Peers;
            foreach (var peer in peers)
            {
                if (!peer.KeepAlive || ct.IsCancellationRequested) break;

                var (ok, err) = await _sender.SendKeepAliveAsync(peer).ConfigureAwait(false);
                if (!ok)
                    _state.Emit(LogLevel.Warn,
                        $"Keep-awake ping to {peer.Name} failed: {err}");
            }
        }
    }
}
