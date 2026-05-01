// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Text.Json;

namespace PigeonPost.Services;

/// <summary>
/// Persisted user preferences, serialised to <c>%LOCALAPPDATA%\PigeonPost\settings.json</c>.
/// </summary>
public class AppSettings
{
    /// <summary>Absolute path to the folder where received files are saved.</summary>
    public string DownloadsFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// UI theme preference: <c>"Light"</c>, <c>"Dark"</c>, or <c>"System"</c> (follow Windows).
    /// Defaults to <c>"System"</c> so first-run users see no behaviour change.
    /// </summary>
    public string Theme { get; set; } = "System";
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file in the per-user local app-data folder.
/// All members are static; there is exactly one <see cref="Current"/> instance per process.
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PigeonPost");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions WriteOptions =
        new() { WriteIndented = true };

    /// <summary>The active settings for this process. Modified in-place by the Settings dialog.</summary>
    public static AppSettings Current { get; private set; } = Load();

    /// <summary>
    /// Reads settings from disk. Returns defaults when the file does not exist or is corrupt.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* return defaults on any IO / parse error */ }
        return new AppSettings();
    }

    /// <summary>Persists <see cref="Current"/> to disk. Failures are silently ignored.</summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Current, WriteOptions));
        }
        catch { /* best-effort save */ }
    }
}
