using System;
using System.Threading;
using PigeonPost.Models;

namespace PigeonPost.Services;

/// <summary>
/// Process-wide mutable state shared between the HTTP listener (background threads)
/// and the ViewModel (UI thread).
///
/// Counter fields use <see cref="Interlocked"/> operations so background threads can
/// increment them without a lock. The <see cref="Paused"/> flag is declared volatile
/// so reads on background threads always see the latest value written by the UI thread.
/// </summary>
public sealed class AppState
{
    // Backing fields for counters — incremented atomically from any thread.
    private int _filesReceived;
    private int _clipboardSends;
    private int _clipboardReceives;
    private int _clipboardClears;

    // volatile ensures the background HTTP handler always sees the current pause state
    // without needing a lock (bool reads/writes are atomic on x64, but volatile adds
    // the required memory-ordering guarantee).
    private volatile bool _paused;

    /// <summary>
    /// When <see langword="true"/> the HTTP handler returns <c>503</c> to every request.
    /// Written on the UI thread; read on background HTTP handler threads.
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Moment the application was launched; used to calculate uptime.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    // ---- Read-only counter snapshots (safe to read from any thread) ----

    public int FilesReceived    => _filesReceived;
    public int ClipboardSends   => _clipboardSends;
    public int ClipboardReceives=> _clipboardReceives;
    public int ClipboardClears  => _clipboardClears;

    // ---- Events raised after each state change (subscribed by the ViewModel) ----

    /// <summary>Raised (from any thread) whenever a new log entry is available.</summary>
    public event Action<LogEntry>? LogEntryAdded;

    /// <summary>Raised (from any thread) whenever a counter changes.</summary>
    public event Action? CountersChanged;

    // ---- Counter increment helpers ----

    public void IncrementFilesReceived()    { Interlocked.Increment(ref _filesReceived);    CountersChanged?.Invoke(); }
    public void IncrementClipboardSends()   { Interlocked.Increment(ref _clipboardSends);   CountersChanged?.Invoke(); }
    public void IncrementClipboardReceives(){ Interlocked.Increment(ref _clipboardReceives); CountersChanged?.Invoke(); }
    public void IncrementClipboardClears()  { Interlocked.Increment(ref _clipboardClears);  CountersChanged?.Invoke(); }

    /// <summary>
    /// Creates a <see cref="LogEntry"/> and fires <see cref="LogEntryAdded"/>.
    /// Safe to call from any thread.
    /// </summary>
    public void Emit(LogLevel level, string message)
        => LogEntryAdded?.Invoke(new LogEntry(level, message));
}