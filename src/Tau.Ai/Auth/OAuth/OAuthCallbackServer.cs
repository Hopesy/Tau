using System.Net;
using System.Text;

namespace Tau.Ai.Auth.OAuth;

public sealed class OAuthCallbackServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly string _expectedState;
    private readonly TaskCompletionSource<(string Code, string State)?> _codeReceived = new();
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private Task? _listenTask;

    private OAuthCallbackServer(HttpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        _listener = listener;
        _expectedState = expectedState;
        _cancellationRegistration = cancellationToken.Register(() => _codeReceived.TrySetResult(null));
    }

    public static OAuthCallbackServer Start(int port, string path, string expectedState, CancellationToken cancellationToken = default)
    {
        var host = Environment.GetEnvironmentVariable("TAU_OAUTH_CALLBACK_HOST") ?? "127.0.0.1";
        var prefix = $"http://{host}:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var server = new OAuthCallbackServer(listener, expectedState, cancellationToken);
        server._listenTask = server.ListenAsync(path);
        return server;
    }

    public string RedirectUri(int port, string path)
    {
        var host = Environment.GetEnvironmentVariable("TAU_OAUTH_CALLBACK_HOST") ?? "localhost";
        return $"http://{host}:{port}{path}";
    }

    public Task<(string Code, string State)?> WaitForCodeAsync() => _codeReceived.Task;

    public void Cancel() => _codeReceived.TrySetResult(null);

    public async ValueTask DisposeAsync()
    {
        Cancel();
        _listener.Stop();
        _listener.Close();
        _cancellationRegistration.Dispose();
        if (_listenTask is not null)
        {
            try { await _listenTask.ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        }
    }

    private async Task ListenAsync(string expectedPath)
    {
        while (_listener.IsListening && !_codeReceived.Task.IsCompleted)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }

            var request = context.Request;
            var response = context.Response;

            try
            {
                if (!string.Equals(request.Url?.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(response, 404, ErrorHtml("Callback route not found.")).ConfigureAwait(false);
                    continue;
                }

                var code = request.QueryString["code"];
                var state = request.QueryString["state"];
                var error = request.QueryString["error"];

                if (!string.IsNullOrEmpty(error))
                {
                    await WriteResponseAsync(response, 400, ErrorHtml($"Authentication error: {WebUtility.HtmlEncode(error)}")).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                {
                    await WriteResponseAsync(response, 400, ErrorHtml("Missing code or state parameter.")).ConfigureAwait(false);
                    continue;
                }

                if (!string.Equals(state, _expectedState, StringComparison.Ordinal))
                {
                    await WriteResponseAsync(response, 400, ErrorHtml("State mismatch.")).ConfigureAwait(false);
                    continue;
                }

                await WriteResponseAsync(response, 200, SuccessHtml("Authentication completed. You can close this window.")).ConfigureAwait(false);
                _codeReceived.TrySetResult((code, state));
            }
            catch
            {
                try { response.Close(); } catch { }
            }
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, int statusCode, string html)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static string SuccessHtml(string message) =>
        $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Success</title>
        <style>body{font-family:system-ui;display:grid;place-items:center;min-height:100vh;margin:0;background:#0b1020;color:#e8edf7}
        .card{text-align:center;padding:40px;border-radius:16px;background:#0e152b;border:1px solid rgba(255,255,255,.08)}
        h1{color:#54d395;margin:0 0 12px}</style></head>
        <body><div class="card"><h1>&#10003;</h1><p>{{WebUtility.HtmlEncode(message)}}</p></div></body></html>
        """;

    private static string ErrorHtml(string message) =>
        $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Error</title>
        <style>body{font-family:system-ui;display:grid;place-items:center;min-height:100vh;margin:0;background:#0b1020;color:#e8edf7}
        .card{text-align:center;padding:40px;border-radius:16px;background:#0e152b;border:1px solid rgba(255,255,255,.08)}
        h1{color:#ff9da5;margin:0 0 12px}</style></head>
        <body><div class="card"><h1>&#10007;</h1><p>{{WebUtility.HtmlEncode(message)}}</p></div></body></html>
        """;
}
