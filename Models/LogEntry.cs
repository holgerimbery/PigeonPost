// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace PigeonPost.Models;

/// <summary>
/// Immutable record of a single activity-log event.
/// Instances are created on background threads and data-bound directly in the ListView.
/// All computed properties are pure (no UI-thread dependencies) so they are safe to
/// evaluate on any thread.
/// </summary>
public sealed class LogEntry
{
    /// <summary>Wall-clock time at which the event was captured.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Severity / category of the event.</summary>
    public LogLevel Level { get; }

    /// <summary>Human-readable description of what happened.</summary>
    public string Message { get; }

    public LogEntry(LogLevel level, string message)
    {
        At      = DateTimeOffset.Now;
        Level   = level;
        Message = message;
    }

    // ---- Computed binding helpers (evaluated on the UI thread by the ListView) ----

    /// <summary>Formatted timestamp shown in the first column of each log row.</summary>
    public string TimeText => At.LocalDateTime.ToString("HH:mm:ss");

    /// <summary>Fixed-width label shown in the second column.</summary>
    public string LevelLabel => Level switch
    {
        LogLevel.File      => "FILE",
        LogLevel.Clipboard => "CLIPBOARD",
        LogLevel.Info      => "INFO",
        LogLevel.Warn      => "WARN",
        LogLevel.Error     => "ERROR",
        LogLevel.Success   => "OK",
        _                  => "INFO",
    };

    /// <summary>
    /// Returns the <c>App.xaml</c> ThemeDictionary resource key for the level label foreground.
    /// <c>HexColorConverter</c> resolves the key against <c>Application.Current.Resources</c>
    /// at binding time, so the correct Light or Dark GitHub Primer colour is always returned.
    /// </summary>
    public string LevelColor => Level switch
    {
        LogLevel.File      => "LogFileBrush",
        LogLevel.Clipboard => "LogClipboardBrush",
        LogLevel.Warn      => "LogWarnBrush",
        LogLevel.Error     => "LogErrorBrush",
        LogLevel.Success   => "LogSuccessBrush",
        _                  => "LogInfoBrush",
    };
}