// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using PigeonPost.Models;
using PigeonPost.Services;
using Windows.ApplicationModel.DataTransfer;

namespace PigeonPost.ViewModels;

/// <summary>
/// ViewModel for <c>PeersWindow</c>. Manages two collections of peers:
/// <list type="bullet">
///   <item><term><see cref="DiscoveredPeers"/></term><description>Ephemeral — populated by mDNS browse events.</description></item>
///   <item><term><see cref="SavedPeers"/></term><description>Persisted — loaded from / saved to settings.json.</description></item>
/// </list>
/// All mDNS events arrive on a thread-pool thread; they are marshalled to the UI
/// thread via <see cref="_ui"/> before touching the observable collections.
/// </summary>
public sealed partial class PeersViewModel : ObservableObject
{
    private readonly AppState        _state;
    private readonly MdnsService     _mdns;
    private readonly PeerSendService _sender;
    private readonly DispatcherQueue _ui;

    // ---------------------------------------------------------------- observable state

    /// <summary>Peers discovered via mDNS on the current network. Not persisted.</summary>
    public ObservableCollection<PeerEntry> DiscoveredPeers { get; } = [];

    /// <summary>Manually added or permanently saved peers.</summary>
    public ObservableCollection<PeerEntry> SavedPeers { get; } = [];

#pragma warning disable MVVMTK0045
    [ObservableProperty]
    private string _statusMessage = string.Empty;
#pragma warning restore MVVMTK0045

    // ---------------------------------------------------------------- construction

    public PeersViewModel(AppState state, MdnsService mdns, PeerSendService sender, DispatcherQueue ui)
    {
        _state  = state;
        _mdns   = mdns;
        _sender = sender;
        _ui     = ui;

        // Populate saved peers from settings.
        foreach (var peer in SettingsService.Current.Peers)
            SavedPeers.Add(peer);

        // mDNS browse events are subscribed lazily via StartBrowsing() / StopBrowsing()
        // so the multicast listener is only active while the Peers window is visible.
    }

    // ---------------------------------------------------------------- browse lifecycle

    /// <summary>
    /// Subscribes to mDNS browse events and starts the underlying browse loop.
    /// Call when the Peers window becomes visible. Safe to call multiple times.
    /// </summary>
    public void StartBrowsing()
    {
        _mdns.PeerDiscovered += OnPeerDiscovered;
        _mdns.PeerLost       += OnPeerLost;
        _mdns.StartBrowsing();
    }

    /// <summary>
    /// Unsubscribes from mDNS browse events and stops the browse loop.
    /// Clears the <see cref="DiscoveredPeers"/> list. Safe to call multiple times.
    /// </summary>
    public void StopBrowsing()
    {
        _mdns.PeerDiscovered -= OnPeerDiscovered;
        _mdns.PeerLost       -= OnPeerLost;
        _mdns.StopBrowsing();
        _ui.TryEnqueue(DiscoveredPeers.Clear);
    }

    // ---------------------------------------------------------------- mDNS handlers (marshalled to UI thread)

    private void OnPeerDiscovered(DiscoveredPeerInfo info)
    {
        _ui.TryEnqueue(() =>
        {
            // Skip if already in the discovered list or if it matches a saved peer.
            if (DiscoveredPeers.Any(p => p.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase)))
                return;

            DiscoveredPeers.Add(new PeerEntry
            {
                Name            = info.Name,
                Host            = info.Host,
                Port            = info.Port,
                IsAutoDiscovered = true,
            });
        });
    }

    private void OnPeerLost(string name)
    {
        _ui.TryEnqueue(() =>
        {
            var entry = DiscoveredPeers.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
                DiscoveredPeers.Remove(entry);
        });
    }

    // ---------------------------------------------------------------- send actions

    /// <summary>
    /// Sends the current Windows clipboard text to <paramref name="peer"/>.
    /// Must be called on the UI thread (clipboard access requires it).
    /// </summary>
    public async Task SendClipboardToPeerAsync(PeerEntry peer)
    {
        // Read clipboard on the UI thread before any await.
        string text;
        try
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                StatusMessage = "Clipboard does not contain text.";
                return;
            }
            // No ConfigureAwait — stay on UI thread for the WinRT clipboard operation.
            text = await view.GetTextAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cannot read clipboard: {ex.Message}";
            return;
        }

        StatusMessage = $"Sending clipboard to {peer.Name}…";
        // Switch to thread pool for the HTTP send, then marshal back explicitly.
        var (ok, err) = await _sender.SendClipboardAsync(peer, text).ConfigureAwait(false);
        _ui.TryEnqueue(() => StatusMessage = ok
            ? $"✅ Clipboard sent to {peer.Name}"
            : $"❌ {peer.Name}: {err}");
    }

    /// <summary>Sends <paramref name="filePath"/> to <paramref name="peer"/>.</summary>
    public async Task SendFileToPeerAsync(PeerEntry peer, string filePath)
    {
        StatusMessage = $"Sending file to {peer.Name}…";
        var (ok, err) = await _sender.SendFileAsync(peer, filePath).ConfigureAwait(false);
        _ui.TryEnqueue(() => StatusMessage = ok
            ? $"✅ File sent to {peer.Name}"
            : $"❌ {peer.Name}: {err}");
    }

    // ---------------------------------------------------------------- peer management

    /// <summary>
    /// Adds a new peer to <see cref="SavedPeers"/> and persists settings immediately.
    /// </summary>
    public void AddPeer(string name, string host, int port, string bearerToken)
    {
        if (string.IsNullOrWhiteSpace(host)) return;

        var peer = new PeerEntry
        {
            Name        = string.IsNullOrWhiteSpace(name) ? host : name.Trim(),
            Host        = host.Trim(),
            Port        = port > 0 ? port : Constants.Port,
            BearerToken = bearerToken.Trim(),
        };

        SavedPeers.Add(peer);
        SettingsService.Current.Peers.Add(peer);
        SettingsService.Save();

        StatusMessage = $"Added peer {peer.Name}";
    }

    /// <summary>
    /// Removes a peer from <see cref="SavedPeers"/> and persists settings immediately.
    /// </summary>
    public void RemoveSavedPeer(PeerEntry peer)
    {
        SavedPeers.Remove(peer);
        SettingsService.Current.Peers.Remove(peer);
        SettingsService.Save();

        StatusMessage = $"Removed peer {peer.Name}";
    }

    /// <summary>
    /// Updates an existing saved peer in-place and persists settings immediately.
    /// </summary>
    public void UpdatePeer(PeerEntry peer, string name, string host, int port, string bearerToken)
    {
        if (string.IsNullOrWhiteSpace(host)) return;

        peer.Name        = string.IsNullOrWhiteSpace(name) ? host : name.Trim();
        peer.Host        = host.Trim();
        peer.Port        = port > 0 ? port : Constants.Port;
        peer.BearerToken = bearerToken.Trim();

        SettingsService.Save();
        StatusMessage = $"Updated peer {peer.Name}";
    }

    /// <summary>Persists the current peer list (including KeepAlive flags) to disk.</summary>
    public void SavePeers() => SettingsService.Save();

    // ---------------------------------------------------------------- cleanup

    /// <summary>Stops browsing and unsubscribes from mDNS events. Call when the window is closed.</summary>
    public void Detach() => StopBrowsing();
}
