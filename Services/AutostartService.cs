// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#if STORE_BUILD
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
#else
using System;
using Microsoft.Win32;
#endif

namespace PigeonPost.Services;

/// <summary>
/// Manages the "Start with Windows" autostart setting for PigeonPost.
///
/// <para>
/// <b>Winget/Velopack build:</b> uses
/// <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run</c> — no administrator
/// rights required. The registered command includes the <c>--autostart</c> flag so
/// the application can detect it was launched by Windows and start hidden in the tray.
/// </para>
///
/// <para>
/// <b>Store/MSIX build:</b> uses <see cref="StartupTask"/> API (declared in
/// <c>Package.appxmanifest</c> as <c>uap5:StartupTask</c> with
/// <c>TaskId="PigeonPostStartupTask"</c>). The OS shows a consent UI on first enable.
/// Registry access is not permitted in the MSIX sandbox.
/// </para>
/// </summary>
public static class AutostartService
{
#if STORE_BUILD

    // ── Store / MSIX build — StartupTask API ─────────────────────────────────

    private const string TaskId = "PigeonPostStartupTask";

    /// <summary>
    /// Returns <c>true</c> when the StartupTask is currently enabled.
    /// Returns <c>false</c> on any error (task not found, policy disabled, etc.).
    /// </summary>
    public static async Task<bool> GetEnabledAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId).AsTask().ConfigureAwait(false);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch { return false; }
    }

    /// <summary>
    /// Enables or disables the StartupTask.
    /// Enabling may show a system consent dialog; the result is returned but not
    /// surfaced to the user (best-effort).
    /// </summary>
    public static async Task SetEnabledAsync(bool enable)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId).AsTask().ConfigureAwait(false);
            if (enable)
                await task.RequestEnableAsync().AsTask().ConfigureAwait(false);
            else
                task.Disable();
        }
        catch { /* policy or sandbox restriction — silently skip */ }
    }

#else

    // ── Winget / Velopack build — registry-based autostart ───────────────────

    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName    = "PigeonPost";

    /// <summary>Returns <c>true</c> when an autostart entry exists in the registry.</summary>
    public static bool GetEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Enables or disables autostart.
    /// Enabling writes the current EXE path (+ <c>--autostart</c>) to the registry;
    /// disabling removes the value.
    /// </summary>
    public static void SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RegistryPath);

            if (enable)
                key.SetValue(ValueName, BuildRegistrationValue());
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* registry unavailable in some sandbox environments — silently skip */ }
    }

    /// <summary>
    /// Called at startup to keep the registry path current.
    /// If autostart is enabled but the stored path differs from the current EXE path
    /// (e.g. the app was moved or updated), the entry is silently re-registered.
    /// </summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            if (key is null) return;

            if (key.GetValue(ValueName) is not string stored) return; // autostart off

            var current = BuildRegistrationValue();
            if (!string.Equals(stored, current, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, current);
        }
        catch { /* ignore — best-effort path refresh */ }
    }

    /// <summary>
    /// Builds the registry value: the quoted EXE path followed by <c>--autostart</c>.
    /// The <c>--autostart</c> flag lets the app detect it was launched by Windows
    /// and suppress the main window (tray-only start).
    /// </summary>
    private static string BuildRegistrationValue()
    {
        var exe = Environment.ProcessPath
               ?? Environment.GetCommandLineArgs()[0];

        // Quote the path in case it contains spaces.
        return $"\"{exe}\" --autostart";
    }

#endif
}

