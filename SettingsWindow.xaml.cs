// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PigeonPost.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PigeonPost;

/// <summary>
/// Standalone settings window. Replaces the former <c>SettingsDialog</c> ContentDialog.
///
/// Theme changes are applied immediately for live preview.
/// Clicking Cancel (or closing the window without saving) reverts the theme to
/// whatever it was when the window was opened.
/// Clicking Save persists all settings to <c>settings.json</c> and fires
/// <see cref="SettingsSaved"/>.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>Raised when the user clicks Save and all settings have been persisted.</summary>
    public event EventHandler? SettingsSaved;

    /// <summary>Callback that applies a theme string to the main window for live preview.</summary>
    private readonly Action<string> _applyMainTheme;

    /// <summary>Theme that was active when the window was opened (used to revert on cancel).</summary>
    private readonly string _originalTheme;

    /// <summary>Set to <c>true</c> when the user clicks Save so the Closing handler knows not to revert.</summary>
    private bool _saved;

    /// <summary>
    /// Initialises the settings window and pre-populates all controls from
    /// <see cref="SettingsService.Current"/>.
    /// </summary>
    /// <param name="applyMainTheme">
    /// Delegate that applies a theme string ("Light"/"Dark"/"System") to the main window
    /// and the activity-log window for live preview.
    /// </param>
    public SettingsWindow(Action<string> applyMainTheme)
    {
        _applyMainTheme = applyMainTheme;
        _originalTheme  = SettingsService.Current.Theme;

        InitializeComponent();

        try { AppWindow?.Resize(new Windows.Graphics.SizeInt32(460, 480)); }
        catch { /* AppWindow unavailable in some test hosts */ }

        // Pre-populate controls from current settings.
#if STORE_BUILD
        // Store build: StartupTask.GetAsync() is async; fire-and-forget from constructor.
        _ = LoadAutostartStateAsync();

        // Hide the entire Updates section — the Microsoft Store manages updates.
        UpdatesSection.Visibility = Visibility.Collapsed;
#else
        AutostartSwitch.IsOn   = AutostartService.GetEnabled();
        IncludeBetaSwitch.IsOn = SettingsService.Current.IncludeBetaUpdates;
#endif
        DownloadsFolderBox.Text   = SettingsService.Current.DownloadsFolder;
        RequireAuthSwitch.IsOn    = SettingsService.Current.AuthEnabled;
        AuthTokenBox.Text         = SettingsService.Current.AuthToken;
        AllowKeepAwakeSwitch.IsOn            = SettingsService.Current.AllowKeepAwake;
        SenderNamesBox.Text                  = string.Join("\n", SettingsService.Current.KeepAwakeSenders);
        ExcludeVirtualAdaptersSwitch.IsOn    = SettingsService.Current.ExcludeVirtualAdapters;

        // Select the matching theme radio without triggering the live-preview handler.
        ThemeRadios.SelectionChanged -= ThemeRadios_SelectionChanged;
        SelectThemeRadio(SettingsService.Current.Theme);
        ThemeRadios.SelectionChanged += ThemeRadios_SelectionChanged;

        // Revert theme if the window is closed without saving.
        if (AppWindow != null)
            AppWindow.Closing += OnClosing;
    }

    // ── Closing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reverts the theme to its pre-settings state when the window is closed without
    /// having been saved (i.e. via the title-bar X or the Cancel button).
    /// </summary>
    private void OnClosing(Microsoft.UI.Windowing.AppWindow sender,
                           Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!_saved)
        {
            _applyMainTheme(_originalTheme);
            ApplyLocalTheme(_originalTheme);
        }
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
#if STORE_BUILD
        await AutostartService.SetEnabledAsync(AutostartSwitch.IsOn);
#else
        AutostartService.SetEnabled(AutostartSwitch.IsOn);
#endif

        SettingsService.Current.DownloadsFolder    = DownloadsFolderBox.Text;
        SettingsService.Current.Theme              = SelectedThemeTag();
        SettingsService.Current.AuthEnabled        = RequireAuthSwitch.IsOn;
        SettingsService.Current.AuthToken          = AuthTokenBox.Text;
        SettingsService.Current.AllowKeepAwake            = AllowKeepAwakeSwitch.IsOn;
        SettingsService.Current.KeepAwakeSenders           =
            [.. SenderNamesBox.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)];
        SettingsService.Current.ExcludeVirtualAdapters     = ExcludeVirtualAdaptersSwitch.IsOn;
#if !STORE_BUILD
        SettingsService.Current.IncludeBetaUpdates = IncludeBetaSwitch.IsOn;
#endif
        SettingsService.Save();

        _saved = true;
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    // ── Cancel ───────────────────────────────────────────────────────────────

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    // ── Theme live preview ────────────────────────────────────────────────────

    /// <summary>
    /// Applies the selected theme immediately to both the main window (via callback)
    /// and this window's own root so the user gets a live preview.
    /// </summary>
    private void ThemeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var theme = SelectedThemeTag();
        _applyMainTheme(theme);
        ApplyLocalTheme(theme);
    }

    // ── Browse for folder ─────────────────────────────────────────────────────

    /// <summary>Opens a FolderPicker using this window's own HWND and updates the path box.</summary>
    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();

        // Bind the picker to this window's HWND (no external host window needed).
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            DownloadsFolderBox.Text = folder.Path;
    }

    // ── Update check ─────────────────────────────────────────────────────────

    /// <summary>
    /// Checks for a newer release on GitHub. On the second click (when an update was found)
    /// it downloads and applies the update.
    /// Compiled only for the Winget/Velopack build — Store builds hide this button entirely.
    /// </summary>
    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
#if !STORE_BUILD
        if (CheckUpdateButton.Tag as string != "update-ready")
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.Content   = "Checking…";
            UpdateStatusText.Visibility = Visibility.Collapsed;

            var update = await UpdateService.CheckForUpdatesAsync(IncludeBetaSwitch.IsOn);

            if (update is null)
            {
                UpdateStatusText.Text       = "✅  You're up to date.";
                UpdateStatusText.Visibility = Visibility.Visible;
                CheckUpdateButton.Content   = "Check for Updates";
                CheckUpdateButton.IsEnabled = true;
            }
            else
            {
                var newVer = update.TargetFullRelease?.Version?.ToString() ?? "newer version";
                UpdateStatusText.Text       = $"🆕  Update available: v{newVer}";
                UpdateStatusText.Visibility = Visibility.Visible;
                CheckUpdateButton.Content   = "Download & Install";
                CheckUpdateButton.Tag       = "update-ready";
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateButton.DataContext = update;
            }

            return;
        }

        if (CheckUpdateButton.DataContext is not Velopack.UpdateInfo pendingUpdate) return;

        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content   = "Downloading…";
        UpdateStatusText.Text       = "Downloading update — the app will restart when ready.";

        await UpdateService.DownloadAndApplyAsync(pendingUpdate, pct =>
            DispatcherQueue.TryEnqueue(() =>
                CheckUpdateButton.Content = $"Downloading… {pct}%"));
#endif
    }

#if STORE_BUILD
    /// <summary>
    /// Asynchronously loads the StartupTask state and updates the toggle switch.
    /// Called from the constructor as a fire-and-forget because StartupTask.GetAsync is async.
    /// </summary>
    private async Task LoadAutostartStateAsync()
    {
        AutostartSwitch.IsOn = await AutostartService.GetEnabledAsync();
    }
#endif

    // ── Security ──────────────────────────────────────────────────────────────

    private void CopyTokenButton_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(AuthTokenBox.Text);
        Clipboard.SetContent(dp);
    }

    private void RegenerateTokenButton_Click(object sender, RoutedEventArgs e)
    {
        var newToken = SettingsService.GenerateToken();
        AuthTokenBox.Text                 = newToken;
        SettingsService.Current.AuthToken = newToken;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string SelectedThemeTag() =>
        (ThemeRadios.SelectedItem as RadioButton)?.Tag as string ?? "System";

    private void SelectThemeRadio(string theme)
    {
        foreach (var item in ThemeRadios.Items)
        {
            if (item is RadioButton rb && rb.Tag as string == theme)
            {
                ThemeRadios.SelectedItem = rb;
                return;
            }
        }
        if (ThemeRadios.Items.Count > 0)
            ThemeRadios.SelectedIndex = ThemeRadios.Items.Count - 1;
    }

    /// <summary>Applies the theme to this window's own root grid.</summary>
    private void ApplyLocalTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark"  => ElementTheme.Dark,
            _       => ElementTheme.Default,
        };
    }

    /// <summary>Applies the theme to this window's root grid (called from MainWindow).</summary>
    public void ApplyTheme(ElementTheme theme) => RootGrid.RequestedTheme = theme;
}
