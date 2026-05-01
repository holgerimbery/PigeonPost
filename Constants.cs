// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using PigeonPost.Services;

namespace PigeonPost;

/// <summary>
/// Application-wide constants that are fixed for the lifetime of the process.
/// </summary>
public static class Constants
{
    /// <summary>TCP port the embedded HTTP listener binds to.</summary>
    public const int Port = 2560;

    /// <summary>
    /// Absolute path to the folder where received files are saved.
    /// Delegates to <see cref="SettingsService.Current"/> so the value updates
    /// immediately after the user changes it in the Settings dialog.
    /// </summary>
    public static string DownloadsFolder => SettingsService.Current.DownloadsFolder;
}