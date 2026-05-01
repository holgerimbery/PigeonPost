using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MUC = Microsoft.UI.Colors;

namespace PigeonPost.Models;

/// <summary>
/// Immutable record of a single activity-log event.
/// Instances are created on background threads and data-bound directly in the ListView,
/// so all computed properties must be thread-safe and side-effect-free.
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

    /// <summary>Fixed-width label shown in the second column, colour-coded by level.</summary>
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
    /// Foreground brush applied to the level label, colour-coded by log level.
    /// Looks up the brush from <c>Application.Current.Resources</c> so it automatically
    /// returns the Light or Dark variant that matches the current Windows theme.
    /// Falls back to hardcoded colours if the resource dictionary is not yet loaded.
    /// </summary>
    public Brush LevelBrush
    {
        get
        {
            var key = Level switch
            {
                LogLevel.File      => "LogFileBrush",
                LogLevel.Clipboard => "LogClipboardBrush",
                LogLevel.Warn      => "LogWarnBrush",
                LogLevel.Error     => "LogErrorBrush",
                LogLevel.Success   => "LogSuccessBrush",
                _                  => "LogInfoBrush",
            };

            // TryGetValue on ResourceDictionary with ThemeDictionaries returns
            // the variant for the currently active theme (Light or Dark).
            if (Application.Current?.Resources.TryGetValue(key, out var brushObj) == true
                && brushObj is Brush b)
                return b;

            // Fallback: hardcoded colours used before XAML resources are fully loaded.
            return new SolidColorBrush(Level switch
            {
                LogLevel.File      => MUC.MediumPurple,
                LogLevel.Clipboard => MUC.DodgerBlue,
                LogLevel.Warn      => MUC.Goldenrod,
                LogLevel.Error     => MUC.OrangeRed,
                LogLevel.Success   => MUC.SeaGreen,
                _                  => MUC.Gray,
            });
        }
    }
}