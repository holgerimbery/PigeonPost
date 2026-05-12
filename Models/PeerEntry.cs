// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace PigeonPost.Models;

/// <summary>
/// A remote PigeonPost peer that this PC can push clipboard content and files to.
/// Instances that are saved to disk live in <see cref="Services.AppSettings.Peers"/>;
/// mDNS-discovered instances are held only in memory.
/// </summary>
public sealed class PeerEntry
{
    /// <summary>Human-readable display name (e.g. the remote machine name).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Hostname or IP address used to reach the peer
    /// (e.g. <c>DESKTOP-ABC.local</c> or <c>192.168.1.42</c>).
    /// </summary>
    public string Host { get; set; } = "";

    /// <summary>TCP port the peer's PigeonPost HTTP server listens on. Default: 2560.</summary>
    public int Port { get; set; } = 2560;

    /// <summary>
    /// DPAPI-encrypted bearer token, stored as a Base64 string.
    /// Written to <c>settings.json</c> by <see cref="Services.SettingsService.Save"/>.
    /// Empty string means no authentication is required for this peer.
    /// </summary>
    public string BearerTokenProtected { get; set; } = "";

    /// <summary>
    /// Plaintext bearer token. <b>Never serialised to disk.</b>
    /// Populated at runtime by decrypting <see cref="BearerTokenProtected"/> in
    /// <see cref="Services.SettingsService.Load"/>.
    /// </summary>
    [JsonIgnore]
    public string BearerToken { get; set; } = "";

    /// <summary>
    /// <c>true</c> when this entry was auto-discovered via mDNS rather than added manually.
    /// Not persisted — entries are rebuilt from live mDNS traffic each session.
    /// </summary>
    [JsonIgnore]
    public bool IsAutoDiscovered { get; set; }

    /// <summary>
    /// Formatted <c>host:port</c> string for display in the UI.
    /// Not persisted.
    /// </summary>
    [JsonIgnore]
    public string HostPort => $"{Host}:{Port}";
}
