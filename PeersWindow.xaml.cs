// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PigeonPost.Models;
using PigeonPost.Services;
using PigeonPost.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PigeonPost;

/// <summary>
/// Window for managing remote PigeonPost peers and sending clipboard/files to them.
/// A single instance is kept alive by <see cref="MainWindow"/>; subsequent open requests
/// re-activate the existing window rather than creating a duplicate.
/// </summary>
public sealed partial class PeersWindow : Window
{
    public PeersViewModel ViewModel { get; }

    public PeersWindow(AppState state, MdnsService mdns, PeerSendService sender)
    {
        ViewModel = new PeersViewModel(state, mdns, sender, DispatcherQueue);
        InitializeComponent();
        Title = "Peers — PigeonPost";

        if (AppWindow != null)
        {
            try
            {
                var scale = GetScaleFactor();
                AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)(580 * scale), (int)(700 * scale)));

                if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
                    op.IsMaximizable = false;
            }
            catch { /* best-effort sizing */ }

            AppWindow.Closing += (_, _) => ViewModel.Detach();
        }
    }

    /// <summary>Applies a WinUI element theme so this window stays in sync with the main window.</summary>
    public void ApplyTheme(ElementTheme theme) => RootGrid.RequestedTheme = theme;

    // ---------------------------------------------------------------- button handlers

    private async void SendClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PeerEntry peer)
            await ViewModel.SendClipboardToPeerAsync(peer);
    }

    private async void SendFileButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PeerEntry peer) return;

        var path = await PickFileAsync();
        if (path is null) return;

        await ViewModel.SendFileToPeerAsync(peer, path);
    }

    private void RemovePeerButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PeerEntry peer)
            ViewModel.RemoveSavedPeer(peer);
    }

    private void AddPeerButton_Click(object sender, RoutedEventArgs e)
    {
        var name  = AddNameBox.Text;
        var host  = AddHostBox.Text;
        var port  = (int)(AddPortBox.Value is double d && !double.IsNaN(d) ? d : Constants.Port);
        var token = AddTokenBox.Password;

        if (string.IsNullOrWhiteSpace(host))
        {
            ViewModel.StatusMessage = "Host / IP address is required.";
            return;
        }

        ViewModel.AddPeer(name, host, port, token);

        // Clear form.
        AddNameBox.Text      = "";
        AddHostBox.Text      = "";
        AddPortBox.Value     = Constants.Port;
        AddTokenBox.Password = "";
    }

    // ---------------------------------------------------------------- file picker

    private async Task<string?> PickFileAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");

            // Associate the picker with this window's HWND (required for unpackaged WinUI 3).
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- DPI helpers

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
