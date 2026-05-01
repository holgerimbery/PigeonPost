using System;
using System.IO;

namespace PigeonPost;

/// <summary>
/// Application-wide constants that are fixed for the lifetime of the process.
/// </summary>
public static class Constants
{
    /// <summary>TCP port the embedded HTTP listener binds to.</summary>
    public const int Port = 2560;

    /// <summary>
    /// Absolute path to the current user's Downloads folder.
    /// Files sent via the API are saved here.
    /// On most Windows systems this resolves to %USERPROFILE%\Downloads.
    /// </summary>
    public static string DownloadsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");
}