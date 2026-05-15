// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using PigeonPost.Models;

namespace PigeonPost.Services;

/// <summary>
/// Prevents Windows from activating the screensaver or turning off the display by
/// calling <c>SetThreadExecutionState</c> with
/// <c>ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED | ES_CONTINUOUS</c>.
///
/// <para>
/// The receiver must explicitly opt in via <see cref="AppSettings.AllowKeepAwake"/>.
/// Call <see cref="Ping"/> each time a keep-awake HTTP request arrives; a built-in
/// watchdog re-enables normal sleep after <see cref="WatchdogSeconds"/> of silence
/// so the PC is never permanently locked awake.
/// </para>
/// </summary>
public sealed class KeepAwakeService : IDisposable
{
    // ---- Win32 constants -------------------------------------------------------

    // ES_SYSTEM_REQUIRED  = 0x00000001 — keep system running
    // ES_DISPLAY_REQUIRED = 0x00000002 — keep display on and block screensaver
    // ES_CONTINUOUS       = 0x80000000 — make the state persist until changed
    private const uint ES_CONTINUOUS       = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED  = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    private const uint ES_AWAKE         = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;
    private const uint ES_SLEEP_ALLOWED = ES_CONTINUOUS;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    // ---- Configuration --------------------------------------------------------

    /// <summary>
    /// Seconds after the last <see cref="Ping"/> before sleep is automatically re-enabled.
    /// 90 s gives two missed 30 s pings before the watchdog fires.
    /// </summary>
    public const int WatchdogSeconds = 90;

    // ---- State ----------------------------------------------------------------

    private readonly AppState _state;
    private Timer? _watchdog;
    private bool   _isActive;
    private readonly object _lock = new();

    public KeepAwakeService(AppState state) => _state = state;

    // ---- Public API -----------------------------------------------------------

    /// <summary>
    /// Resets the watchdog timer and, if not already active, calls
    /// <c>SetThreadExecutionState</c> to block sleep and the screensaver.
    /// Thread-safe.
    /// </summary>
    public void Ping()
    {
        lock (_lock)
        {
            if (!_isActive)
            {
                SetThreadExecutionState(ES_AWAKE);
                _isActive = true;
                _state.Emit(LogLevel.Info, "Keep-awake: display and sleep prevention active");
            }

            // Reset or create the watchdog so it fires WatchdogSeconds after the last ping.
            if (_watchdog == null)
                _watchdog = new Timer(_ => Disable(), null,
                    TimeSpan.FromSeconds(WatchdogSeconds), Timeout.InfiniteTimeSpan);
            else
                _watchdog.Change(TimeSpan.FromSeconds(WatchdogSeconds), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Immediately re-enables normal sleep and screensaver behaviour and stops the watchdog.
    /// Safe to call when already inactive (no-op in that case). Thread-safe.
    /// </summary>
    public void Disable()
    {
        lock (_lock)
        {
            if (!_isActive) return;

            SetThreadExecutionState(ES_SLEEP_ALLOWED);
            _isActive = false;
            _watchdog?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _state.Emit(LogLevel.Info, "Keep-awake: sleep and screensaver re-enabled");
        }
    }

    /// <summary>Whether display/sleep prevention is currently active.</summary>
    public bool IsActive { get { lock (_lock) return _isActive; } }

    /// <inheritdoc/>
    public void Dispose()
    {
        Disable();
        lock (_lock)
        {
            _watchdog?.Dispose();
            _watchdog = null;
        }
    }
}
