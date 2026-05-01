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
    /// Hex colour string for the level label foreground.
    /// Returns a value safe for use with <c>x:Bind</c> in a DataTemplate without
    /// touching any WinUI / XAML types — avoids the threading and resource-lookup
    /// issues that arise when returning a <c>Brush</c> from inside a compiled binding.
    /// The XAML side converts this to a <see cref="Microsoft.UI.Xaml.Media.SolidColorBrush"/>
    /// via <c>HexColorConverter</c>.
    /// Light-mode colours are used; they look fine on both light and dark Mica backgrounds.
    /// </summary>
    public string LevelColor => Level switch
    {
        LogLevel.File      => "#8250df",   // purple
        LogLevel.Clipboard => "#0969da",   // blue
        LogLevel.Warn      => "#9a6700",   // amber
        LogLevel.Error     => "#cf222e",   // red
        LogLevel.Success   => "#1a7f37",   // green
        _                  => "#57606a",   // grey  (Info)
    };
}