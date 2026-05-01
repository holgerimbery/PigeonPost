// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace PigeonPost.Services;

/// <summary>
/// Checks for new PigeonPost releases on GitHub and applies them via Velopack.
///
/// <para>
/// Update checks are silently skipped when the app is running from a development
/// build (i.e. not installed via the Velopack Setup.exe), so local dev workflows
/// are unaffected. In production the check happens once on startup; the result is
/// surfaced to the user as a dismissible banner in <c>MainWindow</c>.
/// </para>
/// </summary>
public static class UpdateService
{
    /// <summary>GitHub repository that hosts the Velopack release assets.</summary>
    private const string GitHubRepo = "https://github.com/holgerimbery/PigeonPost";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks GitHub Releases for a newer version.
    /// Returns <c>null</c> when the app is not installed via Velopack, when the
    /// check fails (no network, rate-limited, etc.), or when already up-to-date.
    /// Never throws — all exceptions are caught and silently swallowed.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            var mgr = CreateManager();

            // IsInstalled is false when running a raw dev build (no Squirrel/Velopack
            // installation directory). Skip the check to avoid confusing errors.
            if (!mgr.IsInstalled) return null;

            return await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch
        {
            // Network errors, GitHub rate-limits, malformed feeds — never crash the app.
            return null;
        }
    }

    /// <summary>
    /// Downloads the pending update, then restarts the application to apply it.
    /// Progress is reported via <paramref name="onProgress"/> (0–100).
    /// Should be called only after <see cref="CheckForUpdatesAsync"/> returns a non-null result.
    /// </summary>
    /// <param name="update">The update to apply (returned by <see cref="CheckForUpdatesAsync"/>).</param>
    /// <param name="onProgress">Optional progress callback (0–100).</param>
    public static async Task DownloadAndApplyAsync(UpdateInfo update,
                                                    Action<int>? onProgress = null)
    {
        var mgr = CreateManager();
        await mgr.DownloadUpdatesAsync(update, onProgress).ConfigureAwait(false);

        // Exit the current process and let Velopack's Update.exe re-launch the
        // newly installed version. This call does not return.
        mgr.ApplyUpdatesAndRestart(update);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="UpdateManager"/> pointed at the GitHub Releases feed.
    /// <c>prerelease: false</c> ensures only stable tags are offered to users.
    /// </summary>
    private static UpdateManager CreateManager() =>
        new(new GithubSource(GitHubRepo, null, prerelease: false));
}
