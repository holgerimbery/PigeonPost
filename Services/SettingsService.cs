// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Security.Cryptography;
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

    /// <summary>
    /// When <c>true</c> the HTTP server requires every request to carry a valid
    /// <c>Authorization: Bearer &lt;token&gt;</c> header. Defaults to <c>false</c>.
    /// </summary>
    public bool AuthEnabled { get; set; } = false;

    /// <summary>
    /// The bearer token clients must send when <see cref="AuthEnabled"/> is <c>true</c>.
    /// Auto-generated on first load; can be regenerated from the Settings dialog.
    /// </summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>
    /// When <c>true</c> the update check also considers pre-release (beta) builds.
    /// Defaults to <c>false</c> so normal users only receive stable releases.
    /// </summary>
    public bool IncludeBetaUpdates { get; set; } = false;
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
    /// Ensures <see cref="AppSettings.AuthToken"/> is always populated.
    /// </summary>
    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                settings = new AppSettings();
            }
        }
        catch { settings = new AppSettings(); }

        // Guarantee a non-empty token exists (first run, or settings file predates this feature).
        if (string.IsNullOrEmpty(settings.AuthToken))
            settings.AuthToken = GenerateToken();

        return settings;
    }

    /// <summary>Generates a cryptographically random URL-safe bearer token.</summary>
    public static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
               .TrimEnd('=')
               .Replace('+', '-')
               .Replace('/', '_');

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
