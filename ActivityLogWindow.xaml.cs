// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PigeonPost.Models;
using PigeonPost.ViewModels;

namespace PigeonPost;

/// <summary>
/// Standalone activity-log window.
/// Displays a filterable view of <see cref="MainViewModel.LogEntries"/> with level
/// toggle buttons and a text-search box.
///
/// Lifecycle: single-instance — the owner (<see cref="MainWindow"/>) creates one
/// instance and keeps it alive for the duration of the app session.
/// Closing the window hides it rather than destroying it; clicking the toolbar
/// button in MainWindow re-activates it.
/// </summary>
public sealed partial class ActivityLogWindow : Window
{
    public MainViewModel ViewModel { get; }

    /// <summary>Filtered subset of <see cref="ViewModel.LogEntries"/> shown in the ListView.</summary>
    public ObservableCollection<LogEntry> FilteredEntries { get; } = new();

    private readonly HashSet<LogLevel> _activeLevels = new((LogLevel[])Enum.GetValues(typeof(LogLevel)));
    private string _searchText = "";

    public ActivityLogWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        try
        {
            var scale = GetScaleFactor();
            AppWindow?.Resize(new Windows.Graphics.SizeInt32(
                (int)(800 * scale), (int)(560 * scale)));
        }
        catch { /* AppWindow unavailable in some test hosts */ }

        // Populate initial filtered list and badge.
        ApplyFilter();

        // Keep the filtered list in sync as new entries arrive.
        ViewModel.LogEntries.CollectionChanged += OnLogEntriesChanged;

        // Hide instead of destroy when the user clicks the window's close button.
        if (AppWindow != null)
            AppWindow.Closing += (_, args) =>
            {
                args.Cancel = true;
                AppWindow.Hide();
            };
    }

    /// <summary>
    /// Applies the requested theme so the log window stays in sync with the
    /// main window whenever the user changes the theme in Settings.
    /// </summary>
    public void ApplyTheme(ElementTheme theme) => RootGrid.RequestedTheme = theme;

    // ── Collection sync ──────────────────────────────────────────────────────

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ApplyFilter();
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (LogEntry entry in e.NewItems)
                if (Matches(entry)) FilteredEntries.Add(entry);
            UpdateBadge();
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            foreach (LogEntry entry in e.OldItems) FilteredEntries.Remove(entry);
            UpdateBadge();
        }
    }

    // ── Filter logic ─────────────────────────────────────────────────────────

    private bool Matches(LogEntry entry) =>
        _activeLevels.Contains(entry.Level) &&
        (_searchText.Length == 0 ||
         entry.Message.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

    private void ApplyFilter()
    {
        FilteredEntries.Clear();
        foreach (var entry in ViewModel.LogEntries.Where(Matches))
            FilteredEntries.Add(entry);
        UpdateBadge();
    }

    private void UpdateBadge()
    {
        var total    = ViewModel.LogEntries.Count;
        var filtered = FilteredEntries.Count;
        LogBadgeText.Text   = filtered < total ? $"{filtered} / {total}" : total.ToString();
        LogBadge.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Filter event handlers ─────────────────────────────────────────────────

    private void LevelToggle_Changed(object sender, RoutedEventArgs e)
    {
        if ((sender as ToggleButton)?.Tag is not string tag) return;
        if (!Enum.TryParse<LogLevel>(tag, out var level)) return;

        if ((sender as ToggleButton)!.IsChecked == true)
            _activeLevels.Add(level);
        else
            _activeLevels.Remove(level);

        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text ?? "";
        ApplyFilter();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) =>
        SearchBox.Text = ""; // triggers SearchBox_TextChanged automatically

    // ── Helpers ───────────────────────────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private double GetScaleFactor()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            return GetDpiForWindow(hwnd) / 96.0;
        }
        catch { return 1.0; }
    }
}
