using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using PigeonPost.Models;
using Windows.ApplicationModel.DataTransfer;

namespace PigeonPost.Services;

/// <summary>
/// Embedded HTTP server that exposes the PigeonPost local API.
///
/// Accepted requests (all via POST to "/"):
///   <list type="bullet">
///     <item><c>clipboard: send</c>    — body text is written to the Windows clipboard.</item>
///     <item><c>clipboard: receive</c> — current clipboard text is returned as the response body.</item>
///     <item><c>clipboard: clear</c>   — clipboard is emptied.</item>
///     <item><c>filename: &lt;name&gt;</c> — binary body is saved to the Downloads folder.</item>
///   </list>
///
/// The listener binds one prefix per local IPv4 address (see <see cref="NetworkHelper"/>)
/// so it accepts traffic on any interface without requiring administrator privileges.
/// </summary>
public sealed class ListenerService : IDisposable
{
    private readonly AppState _state;
    private readonly DispatcherQueue _ui;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;

    /// <param name="state">Shared application state (counters, pause flag, log event).</param>
    /// <param name="ui">UI dispatcher used to marshal clipboard calls onto the UI thread.</param>
    public ListenerService(AppState state, DispatcherQueue ui)
    {
        _state = state;
        _ui    = ui;
    }

    // ----------------------------------------------------------------- start / stop

    /// <summary>
    /// Registers HTTP prefixes and starts the accept loop on a background thread.
    /// Errors during startup (e.g. port already in use) are reported via
    /// <see cref="AppState.Emit"/> and the method returns without throwing.
    /// </summary>
    public void Start()
    {
        // Always bind localhost so loopback requests work even with no network adapters.
        _listener.Prefixes.Add($"http://localhost:{Constants.Port}/");

        // Bind every active IPv4 address to accept traffic from other devices on the LAN.
        foreach (var ip in NetworkHelper.GetAllBindableIPv4())
            _listener.Prefixes.Add($"http://{ip}:{Constants.Port}/");

        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            _state.Emit(LogLevel.Error,
                $"Could not start HTTP listener on port {Constants.Port}: {ex.Message}");
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));

        _state.Emit(LogLevel.Success,
            $"Server started — saving files to {Constants.DownloadsFolder}");
    }

    /// <summary>Stops the HTTP listener and cancels the accept loop.</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); }  catch { /* best-effort */ }
        try { _listener.Stop(); } catch { /* best-effort */ }
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    // ----------------------------------------------------------------- accept loop

    /// <summary>
    /// Continuously dequeues incoming <see cref="HttpListenerContext"/> objects and
    /// dispatches each one to <see cref="HandleAsync"/> on a thread-pool thread.
    /// Exits cleanly when cancellation is requested or the listener is stopped.
    /// </summary>
    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Log unexpected failures so the user sees the server stopped rather
                // than wondering why requests are no longer handled.
                if (!ct.IsCancellationRequested)
                    _state.Emit(LogLevel.Error, $"Accept loop stopped: {ex.Message}");
                break;
            }

            // Handle each request concurrently; never await here to keep accepting.
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    // ----------------------------------------------------------------- request dispatcher

    /// <summary>
    /// Validates the HTTP method and pause state, then routes to the appropriate handler.
    /// All unhandled exceptions are caught and surfaced in the activity log.
    /// </summary>
    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // Only POST is supported — reject anything else up-front.
            if (ctx.Request.HttpMethod != "POST")
            {
                await WriteAsync(ctx, 405, "Method not allowed");
                return;
            }

            // Honour the pause flag; return 503 so the client can detect the paused state.
            if (_state.Paused)
            {
                _state.Emit(LogLevel.Warn, "Request rejected — server is paused");
                await WriteAsync(ctx, 503, "Server is paused");
                return;
            }

            var clipboardAction = ctx.Request.Headers["clipboard"];
            if (!string.IsNullOrEmpty(clipboardAction))
            {
                await HandleClipboardAsync(ctx, clipboardAction);
                return;
            }

            var filename = ctx.Request.Headers["filename"];
            if (!string.IsNullOrEmpty(filename))
            {
                await HandleFileAsync(ctx, filename);
                return;
            }

            // Neither expected header was present.
            _state.Emit(LogLevel.Error, "Bad request — missing filename or clipboard header");
            await WriteAsync(ctx, 400, "Bad request");
        }
        catch (Exception ex)
        {
            _state.Emit(LogLevel.Error, $"Unhandled error: {ex.Message}");
            try { await WriteAsync(ctx, 500, ex.Message); } catch { /* response may already be started */ }
        }
    }

    // --------------------------------------------------------------- clipboard handlers

    /// <summary>
    /// Handles the three clipboard actions: <c>send</c>, <c>clear</c>, <c>receive</c>.
    /// Clipboard reads and writes must happen on the UI thread, so both helpers marshal
    /// via <see cref="DispatcherQueue.TryEnqueue"/>.
    /// </summary>
    private async Task HandleClipboardAsync(HttpListenerContext ctx, string action)
    {
        switch (action.ToLowerInvariant())
        {
            case "send":
            {
                // Read the request body as UTF-8 text and push it to the clipboard.
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var text = await reader.ReadToEndAsync();
                await SetClipboardOnUiAsync(text);
                _state.IncrementClipboardSends();
                _state.Emit(LogLevel.Clipboard,
                    $"Received \u2192 clipboard: \"{Truncate(text, 60)}\"");
                await WriteAsync(ctx, 200, "Data copied to clipboard");
                break;
            }

            case "clear":
            {
                await SetClipboardOnUiAsync(string.Empty);
                _state.IncrementClipboardClears();
                _state.Emit(LogLevel.Clipboard, "Clipboard cleared");
                await WriteAsync(ctx, 200, "Clipboard cleared");
                break;
            }

            case "receive":
            {
                // Return the current clipboard text as the response body.
                var data = await GetClipboardOnUiAsync();
                _state.IncrementClipboardReceives();
                _state.Emit(LogLevel.Clipboard,
                    $"Sent clipboard \u2192 device: \"{Truncate(data, 60)}\"");
                await WriteAsync(ctx, 200, data);
                break;
            }

            default:
                _state.Emit(LogLevel.Error, $"Invalid clipboard action: {action}");
                await WriteAsync(ctx, 400, "Invalid clipboard action");
                break;
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> to the Windows clipboard on the UI thread.
    /// <c>Clipboard.Flush()</c> keeps the data alive after the app exits.
    /// </summary>
    private Task SetClipboardOnUiAsync(string text)
    {
        var tcs = new TaskCompletionSource();

        // TryEnqueue returns false when the DispatcherQueue is shutting down.
        if (!_ui.TryEnqueue(() =>
            {
                try
                {
                    var dp = new DataPackage();
                    dp.SetText(text);
                    Clipboard.SetContent(dp);
                    Clipboard.Flush();
                    tcs.TrySetResult();
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("UI dispatcher is unavailable."));
        }

        return tcs.Task;
    }

    /// <summary>
    /// Reads the current clipboard text on the UI thread and returns it.
    /// Returns <see cref="string.Empty"/> if the clipboard does not contain text.
    /// </summary>
    private Task<string> GetClipboardOnUiAsync()
    {
        var tcs = new TaskCompletionSource<string>();

        // async void lambda is intentional here: DispatcherQueueHandler is void-returning,
        // so we use async void to be able to await GetTextAsync() inside the enqueued work.
        // The entire body is wrapped in try/catch so exceptions are routed through the TCS.
        if (!_ui.TryEnqueue(async () =>
            {
                try
                {
                    var content = Clipboard.GetContent();
                    if (content.Contains(StandardDataFormats.Text))
                    {
                        var text = await content.GetTextAsync();
                        tcs.TrySetResult(text ?? string.Empty);
                    }
                    else
                    {
                        tcs.TrySetResult(string.Empty);
                    }
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("UI dispatcher is unavailable."));
        }

        return tcs.Task;
    }

    // ------------------------------------------------------------------ file handler

    /// <summary>
    /// Saves the request body as a file in <see cref="Constants.DownloadsFolder"/>.
    /// Path-traversal is prevented by stripping all directory components from the filename.
    /// </summary>
    private async Task HandleFileAsync(HttpListenerContext ctx, string filename)
    {
        var contentLength = ctx.Request.ContentLength64;

        // Buffer the entire body into memory before writing to disk.
        using var ms = new MemoryStream();
        await ctx.Request.InputStream.CopyToAsync(ms);
        var data = ms.ToArray();

        // ContentLength64 is -1 when the header is absent; >= 0 means the client
        // declared an explicit length that we can validate.
        if (contentLength >= 0 && data.Length != contentLength)
        {
            _state.Emit(LogLevel.Error, $"Length mismatch on \"{filename}\"");
            await WriteAsync(ctx, 400, "Content length does not match the actual data length");
            return;
        }

        Directory.CreateDirectory(Constants.DownloadsFolder);

        // Strip any directory components to prevent path-traversal attacks
        // (e.g. a filename of "../../evil.exe" becomes "evil.exe").
        var safeName = Path.GetFileName(filename);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            _state.Emit(LogLevel.Error, "Invalid filename");
            await WriteAsync(ctx, 400, "Invalid filename");
            return;
        }

        var savePath = Path.Combine(Constants.DownloadsFolder, safeName);
        await File.WriteAllBytesAsync(savePath, data);

        _state.IncrementFilesReceived();
        _state.Emit(LogLevel.File, $"Saved \"{safeName}\" ({data.Length / 1024.0:N1} KB)");
        await WriteAsync(ctx, 200, "File uploaded successfully");
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Writes a plain-text HTTP response and closes the output stream.</summary>
    private static async Task WriteAsync(HttpListenerContext ctx, int status, string body)
    {
        try
        {
            ctx.Response.StatusCode    = status;
            var bytes                  = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType   = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        finally
        {
            // Always close the stream so the client does not hang waiting for the response.
            try { ctx.Response.OutputStream.Close(); } catch { /* already closed */ }
        }
    }

    /// <summary>
    /// Trims <paramref name="s"/> to at most <paramref name="max"/> characters,
    /// replaces newlines with spaces, and appends "…" when truncation occurs.
    /// </summary>
    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..max] + "\u2026";
    }
}