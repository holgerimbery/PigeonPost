namespace PigeonPost.Models;

/// <summary>
/// Severity / category of a log entry, used to pick colour and label in the activity log.
/// </summary>
public enum LogLevel
{
    /// <summary>General informational message.</summary>
    Info,

    /// <summary>Positive confirmation (e.g. "server started", "server resumed").</summary>
    Success,

    /// <summary>Non-fatal warning (e.g. request rejected while paused).</summary>
    Warn,

    /// <summary>Recoverable error (bad request, unhandled exception in handler).</summary>
    Error,

    /// <summary>A file was received and saved to disk.</summary>
    File,

    /// <summary>A clipboard read, write, or clear operation was performed.</summary>
    Clipboard,
}