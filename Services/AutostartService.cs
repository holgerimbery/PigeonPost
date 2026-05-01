// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Microsoft.Win32;

namespace PigeonPost.Services;

/// <summary>
/// Manages the "Start with Windows" autostart registry entry for PigeonPost.
///
/// <para>
/// Uses <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run</c> — no administrator
/// rights required. The registered command includes the <c>--autostart</c> flag so
/// the application can detect it was launched by Windows and start hidden in the tray.
/// </para>
/// </summary>
public static class AutostartService
{
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName    = "PigeonPost";

    // ── Public API ────────────────────────────────────────────────────────────

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

    // ── Helpers ───────────────────────────────────────────────────────────────

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
}
