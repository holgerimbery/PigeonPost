// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.UI.Xaml;
using PigeonPost.ViewModels;

namespace PigeonPost;

/// <summary>
/// Standalone activity-log window.
/// Displays the same <see cref="MainViewModel.LogEntries"/> collection as the
/// embedded expander did, but as an independent, resizable window with Mica backdrop.
///
/// Lifecycle: single-instance — the owner (<see cref="MainWindow"/>) creates one
/// instance and keeps it alive for the duration of the app session.
/// Closing the window hides it rather than destroying it; clicking the toolbar
/// button in MainWindow re-activates it.
/// </summary>
public sealed partial class ActivityLogWindow : Window
{
    public MainViewModel ViewModel { get; }

    public ActivityLogWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        try { AppWindow?.Resize(new Windows.Graphics.SizeInt32(860, 520)); }
        catch { /* AppWindow unavailable in some test hosts */ }

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
}
