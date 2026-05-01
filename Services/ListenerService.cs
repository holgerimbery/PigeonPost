using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
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
    // Not readonly: after a failed Start() the HttpListener is left in an unusable state
    // and must be replaced with a fresh instance for the localhost-only fallback retry.
    private HttpListener _listener = new();
    private CancellationTokenSource? _cts;

    /// <param name="state">Shared application state (counters, pause flag, log event).</param>
    /// <param name="ui">UI dispatcher used to marshal clipboard calls onto the UI thread.</param>
    public ListenerService(AppState state, DispatcherQueue ui)
    {
        _state = state;
        _ui    = ui;
    }

    // ----------------------------------------------------------------- start / stop

    // Wildcard prefix that covers every network interface and IP address on the machine.
    // HTTP.sys requires a URL ACL reservation for this prefix unless the process is elevated.
    private static readonly string WildcardPrefix = $"http://+:{Constants.Port}/";

    /// <summary>
    /// Registers HTTP prefixes and starts the accept loop on a background thread.
    ///
    /// <para>
    /// Strategy:
    /// <list type="number">
    ///   <item>Try <c>http://+:PORT/</c> — works if the URL ACL is already registered
    ///         or the process is elevated.</item>
    ///   <item>If that fails, attempt a one-time URL ACL registration via an elevated
    ///         <c>netsh</c> process (triggers a UAC prompt).  On success retry step 1.</item>
    ///   <item>If elevation is declined or fails, fall back to <c>localhost</c> only.</item>
    /// </list>
    /// Once the URL ACL is registered it persists across reboots — UAC is only needed once.
    /// </para>
    /// </summary>
    public void Start()
    {
        // ---- Attempt 1: wildcard prefix (works if URL ACL exists or running as admin) ----
        _listener.Prefixes.Add(WildcardPrefix);
        if (TryStartListener())
        {
            // Wildcard succeeded — all interfaces are bound.
            FinishStart(lan: true);
            return;
        }

        // The failed Start() leaves the listener dead; create a fresh instance.
        try { _listener.Close(); } catch { /* already dead */ }
        _listener = new HttpListener();

        // ---- Attempt 2: register URL ACL via elevated netsh, then retry ----
        if (TryRegisterUrlAcl())
        {
            _listener.Prefixes.Add(WildcardPrefix);
            if (TryStartListener())
            {
                FinishStart(lan: true);
                return;
            }
            // netsh succeeded but Start still failed — should not happen; clean up.
            try { _listener.Close(); } catch { }
            _listener = new HttpListener();
        }

        // ---- Attempt 3: localhost-only fallback ----
        _listener.Prefixes.Add($"http://localhost:{Constants.Port}/");
        if (!TryStartListener())
            return;   // Even localhost failed — port probably in use, give up.

        FinishStart(lan: false);
    }

    /// <summary>
    /// Tries to register <c>http://+:PORT/</c> as a URL ACL reservation for Everyone by
    /// launching <c>netsh</c> with the <c>runas</c> verb, which triggers a UAC prompt.
    /// Returns <c>true</c> if the reservation was created (or already existed).
    /// </summary>
    private bool TryRegisterUrlAcl()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "netsh",
                // "user=Everyone" works for all locales; the ACL is machine-wide.
                Arguments       = $"http add urlacl url={WildcardPrefix} user=Everyone",
                Verb            = "runas",   // triggers UAC elevation prompt
                UseShellExecute = true,
                WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden,
                CreateNoWindow  = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(10_000);   // wait up to 10 s for netsh to complete

            // Exit code 0 = created; 183 = already exists — both are success.
            var code = proc?.ExitCode ?? -1;
            if (code is 0 or 183)
            {
                _state.Emit(LogLevel.Info, "URL ACL registered — LAN access enabled permanently");
                return true;
            }

            _state.Emit(LogLevel.Warn, $"netsh urlacl registration returned exit code {code}");
            return false;
        }
        catch (Exception ex)
        {
            // Typically Win32Exception with "The operation was canceled by the user" when
            // the UAC prompt is dismissed, or an IO error if netsh is not found.
            _state.Emit(LogLevel.Warn, $"URL ACL registration skipped: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts the accept loop and emits the startup log line after the listener is bound.
    /// </summary>
    /// <param name="lan"><c>true</c> when the wildcard prefix is active (all interfaces reachable).</param>
    private void FinishStart(bool lan)
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));

        if (lan)
        {
            var ip = NetworkHelper.GetPrimaryLocalIp();
            _state.Emit(LogLevel.Success,
                $"Server started on all interfaces — LAN: http://{ip}:{Constants.Port}/");
        }
        else
        {
            _state.Emit(LogLevel.Warn,
                $"Listening on localhost:{Constants.Port} only " +
                $"(UAC was declined — LAN access unavailable)");
            _state.Emit(LogLevel.Info,
                $"To enable LAN access run once as Administrator, or run: " +
                $"netsh http add urlacl url=http://+:{Constants.Port}/ user=Everyone");
        }
        _state.Emit(LogLevel.Info,
            $"Files saved to: {Constants.DownloadsFolder}");
    }

    /// <summary>
    /// Calls <see cref="HttpListener.Start()"/> and returns <c>true</c> on success.
    /// Logs the error and returns <c>false</c> on failure without throwing.
    /// </summary>
    private bool TryStartListener()
    {
        try
        {
            _listener.Start();
            return true;
        }
        catch (Exception ex)
        {
            // Access denied is expected the first time (no URL ACL); only log at Debug level.
            _state.Emit(LogLevel.Info,
                $"HttpListener.Start failed (will retry): {ex.Message}");
            return false;
        }
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

            // Accept filename from header OR query string (?filename=...).
            // Query-string values are URL-encoded by iOS Shortcuts and decoded automatically
            // by HttpListenerRequest, so special characters (spaces, umlauts, timestamps)
            // never produce an "invalid header" rejection from HTTP.sys.
            var filename = ctx.Request.Headers["filename"]
                        ?? ctx.Request.QueryString["filename"];
            if (!string.IsNullOrEmpty(filename))
            {
                await HandleFileAsync(ctx, filename);
                return;
            }

            // Neither expected header/query param was present.
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
                // Read the request body as UTF-8 text.
                // Accepts three formats so plain curl AND iOS Shortcuts (JSON mode) both work:
                //   1. Plain text:           hello world
                //   2. JSON string literal:  "hello world"        (iOS Shortcuts, no key)
                //   3. JSON object with key: {"text":"hello"}     (iOS Shortcuts, key = text)
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var raw  = await reader.ReadToEndAsync();
                var text = ExtractText(raw);
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

    /// <summary>
    /// Extracts plain text from a request body that may be:
    /// <list type="bullet">
    ///   <item>Plain UTF-8 text  →  returned as-is</item>
    ///   <item>JSON string literal <c>"hello"</c>  →  the unquoted string</item>
    ///   <item>JSON object <c>{"text":"hello"}</c>  →  the value of the "text" key</item>
    /// </list>
    /// This lets iOS Shortcuts in JSON body mode work alongside plain <c>curl -d</c> calls.
    /// </summary>
    private static string ExtractText(string raw)
    {
        var trimmed = raw.Trim();

        // Only attempt JSON parsing if the body looks like JSON.
        if (trimmed.StartsWith('"') || trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                // {"text": "..."} — iOS Shortcuts JSON object with a "text" key.
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("text", out var textProp))
                    return textProp.GetString() ?? string.Empty;

                // "plain string" — JSON string literal.
                if (root.ValueKind == JsonValueKind.String)
                    return root.GetString() ?? string.Empty;
            }
            catch { /* Not valid JSON — fall through and use raw body. */ }
        }

        return raw;
    }
}