// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PigeonPost.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PigeonPost;

/// <summary>
/// Settings ContentDialog: Start with Windows, download folder, and UI theme.
///
/// Theme changes are applied immediately for live preview.
/// Pressing Cancel reverts the theme to whatever it was before the dialog opened.
/// Pressing Save persists all settings to <c>settings.json</c>.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private readonly Window _hostWindow;

    /// <summary>Callback invoked immediately when the user changes the theme radio selection.</summary>
    private readonly Action<string> _applyTheme;

    /// <summary>Theme that was active when the dialog was opened (used to revert on Cancel).</summary>
    private readonly string _originalTheme;

    /// <summary>
    /// Initialises the dialog and pre-populates all controls from <see cref="SettingsService.Current"/>.
    /// </summary>
    /// <param name="hostWindow">The parent window, needed for FolderPicker initialisation.</param>
    /// <param name="applyTheme">
    /// Delegate that applies a theme string ("Light"/"Dark"/"System") to the main window.
    /// </param>
    public SettingsDialog(Window hostWindow, Action<string> applyTheme)
    {
        _hostWindow   = hostWindow;
        _applyTheme   = applyTheme;
        _originalTheme = SettingsService.Current.Theme;

        InitializeComponent();

        // Pre-populate controls from current settings.
        AutostartSwitch.IsOn       = AutostartService.GetEnabled();
        DownloadsFolderBox.Text    = SettingsService.Current.DownloadsFolder;

        // Select the matching theme radio button without triggering the live-preview handler.
        ThemeRadios.SelectionChanged -= ThemeRadios_SelectionChanged;
        SelectThemeRadio(SettingsService.Current.Theme);
        ThemeRadios.SelectionChanged += ThemeRadios_SelectionChanged;

        // Wire the Save (Primary) button.
        PrimaryButtonClick += OnSave;
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists all settings. Called when the user clicks Save.
    /// The theme is already applied live; this only writes to disk and updates service state.
    /// </summary>
    private void OnSave(ContentDialog _, ContentDialogButtonClickEventArgs args)
    {
        // Update autostart registry.
        AutostartService.SetEnabled(AutostartSwitch.IsOn);

        // Update settings object and persist.
        SettingsService.Current.DownloadsFolder = DownloadsFolderBox.Text;
        SettingsService.Current.Theme           = SelectedThemeTag();
        SettingsService.Save();
    }

    // ── Theme live preview ────────────────────────────────────────────────────

    /// <summary>
    /// Applies the selected theme immediately so the user sees a live preview.
    /// Cancelling the dialog reverts via <see cref="_originalTheme"/>.
    /// </summary>
    private void ThemeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _applyTheme(SelectedThemeTag());
    }

    // ── Browse for folder ─────────────────────────────────────────────────────

    /// <summary>Opens a FolderPicker and updates the downloads path text box.</summary>
    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();

        // Required for unpackaged WinUI 3 apps: bind the picker to the window HWND.
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(_hostWindow));

        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            DownloadsFolderBox.Text = folder.Path;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns the <c>Tag</c> string of the currently selected theme radio button.</summary>
    private string SelectedThemeTag() =>
        (ThemeRadios.SelectedItem as RadioButton)?.Tag as string ?? "System";

    /// <summary>Returns the theme string that was active before the dialog was opened.</summary>
    public string OriginalTheme => _originalTheme;

    /// <summary>Selects the radio button whose Tag matches <paramref name="theme"/>.</summary>
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
        // Default to the last item ("Follow Windows") if no match found.
        if (ThemeRadios.Items.Count > 0)
            ThemeRadios.SelectedIndex = ThemeRadios.Items.Count - 1;
    }
}
