// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using PigeonPost.Models;

namespace PigeonPost.Services;

/// <summary>
/// Sends clipboard content and files to a remote <see cref="PeerEntry"/> using the
/// standard PigeonPost HTTP API endpoints.
///
/// <para>
/// The same API that mobile clients use to push to <em>this</em> PC is used in reverse
/// to push from this PC to a peer — both parties run PigeonPost, so no new protocol
/// is required.
/// </para>
///
/// <para>
/// Endpoint: <c>POST http://{host}:{port}/</c><br/>
/// Clipboard: header <c>clipboard: send</c>, body = UTF-8 text.<br/>
/// File:      header <c>filename: {name}</c>, body = raw bytes.
/// </para>
/// </summary>
public sealed class PeerSendService : IDisposable
{
    private readonly AppState _state;
    private readonly HttpClient _http;

    /// <param name="state">Shared application state; used for log emission.</param>
    public PeerSendService(AppState state)
    {
        _state = state;
        _http  = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    // ----------------------------------------------------------------- send methods

    /// <summary>
    /// Sends <paramref name="text"/> to the peer's clipboard via
    /// <c>POST / {clipboard: send}</c>.
    /// </summary>
    /// <returns><c>(true, "")</c> on HTTP 2xx; <c>(false, errorMessage)</c> on any error.</returns>
    public async Task<(bool Ok, string Error)> SendClipboardAsync(PeerEntry peer, string text)
    {
        try
        {
            using var req = BuildRequest(peer);
            req.Method  = HttpMethod.Post;
            req.Headers.Add("clipboard", "send");
            req.Content = new StringContent(text, System.Text.Encoding.UTF8, "text/plain");

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            var ok = resp.IsSuccessStatusCode;

            _state.Emit(ok ? LogLevel.Clipboard : LogLevel.Error,
                ok ? $"→ {peer.Name}: clipboard pushed ({(int)resp.StatusCode})"
                   : $"→ {peer.Name}: clipboard push failed ({(int)resp.StatusCode})");

            return ok ? (true, "") : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            _state.Emit(LogLevel.Error, $"→ {peer.Name}: clipboard push error — {ex.Message}");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Sends the file at <paramref name="filePath"/> to the peer via
    /// <c>POST / {filename: name}</c>.
    /// </summary>
    /// <returns><c>(true, "")</c> on HTTP 2xx; <c>(false, errorMessage)</c> on any error.</returns>
    public async Task<(bool Ok, string Error)> SendFileAsync(PeerEntry peer, string filePath)
    {
        try
        {
            var fileName  = Path.GetFileName(filePath);
            var fileBytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);

            using var req = BuildRequest(peer);
            req.Method  = HttpMethod.Post;
            req.Headers.Add("filename", fileName);
            req.Content = new ByteArrayContent(fileBytes);
            req.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/octet-stream");

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            var ok = resp.IsSuccessStatusCode;

            _state.Emit(ok ? LogLevel.File : LogLevel.Error,
                ok ? $"→ {peer.Name}: '{fileName}' sent ({(int)resp.StatusCode})"
                   : $"→ {peer.Name}: file send failed ({(int)resp.StatusCode})");

            return ok ? (true, "") : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            _state.Emit(LogLevel.Error, $"→ {peer.Name}: file send error — {ex.Message}");
            return (false, ex.Message);
        }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Creates an <see cref="HttpRequestMessage"/> pre-configured with the peer URL and,
    /// when the peer has a bearer token, the <c>Authorization</c> header.
    /// </summary>
    private static HttpRequestMessage BuildRequest(PeerEntry peer)
    {
        var req = new HttpRequestMessage
        {
            RequestUri = new Uri($"http://{peer.Host}:{peer.Port}/"),
        };

        if (!string.IsNullOrEmpty(peer.BearerToken))
            req.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", peer.BearerToken);

        return req;
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();
}
