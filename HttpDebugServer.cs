using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BepInEx.Logging;

namespace Il2cppConsoleMod;

public sealed class HttpDebugServer : IDisposable
{
    private const int MaxBodySize = 10 * 1024 * 1024;

    private readonly IPAddress _listenAddress;
    private readonly int _port;
    private readonly byte[] _expectedTokenHash;
    private readonly ManualLogSource _log;
    private readonly CSharpScriptSession _session;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    public HttpDebugServer(string listenAddress, int port, string token, ManualLogSource log, CSharpScriptSession session)
    {
        _listenAddress = IPAddress.Parse(listenAddress);
        _port = port;
        // Compare SHA-256 digests instead of the raw token so the comparison
        // does not leak timing information about the expected token.
        _expectedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _log = log;
        _session = session;
    }

    public void Start()
    {
        if (_listener != null)
            return;

        var listener = new TcpListener(_listenAddress, _port);
        listener.Start();

        var cts = new CancellationTokenSource();
        var thread = new Thread(() => AcceptLoop(cts.Token))
        {
            IsBackground = true,
            Name = "Il2cppConsoleModHttp"
        };

        try
        {
            thread.Start();
        }
        catch
        {
            listener.Stop();
            throw;
        }

        _listener = listener;
        _cts = cts;
        _thread = thread;

        _log.LogWarning($"Il2cppConsoleMod HTTP server listening on http://{_listenAddress}:{_port}/exec");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts = null;
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private void AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch
            {
                break;
            }

            ThreadPool.QueueUserWorkItem(_ => HandleClient(client, ct));
        }
    }

    private void HandleClient(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var request = ReadHeaders(stream);
                if (request == null)
                    return;

                if (!CheckAuth(request))
                {
                    WriteJson(stream, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }

                // Read the body only after auth succeeds, so unauthenticated
                // clients cannot make us buffer arbitrary amounts of data.
                if (!ReadBody(stream, request))
                    return;

                if (request.Method == "GET" && request.Path == "/health")
                {
                    WriteJson(stream, 200, "{\"status\":\"ok\"}");
                    return;
                }

                if (request.Method == "GET" && request.Path == "/help")
                {
                    var help = "Endpoints (all need header 'X-Debug-Token: <token>'): " +
                               "POST /exec - full program: public static class Payload { public static object Execute() { ... } }. " +
                               "POST /script - interactive session: raw C# statements, state persists across calls, " +
                               "a trailing expression without ';' returns its value. " +
                               "POST /script/reset - clear the interactive session. " +
                               "GET /health, GET /help.";
                    WriteJson(stream, 200, JsonSerializer.Serialize(new { help }));
                    return;
                }

                if (request.Method == "POST" && request.Path == "/script/reset")
                {
                    _session.Reset();
                    WriteJson(stream, 200, "{\"success\":true,\"message\":\"script session reset\"}");
                    return;
                }

                if (request.Method == "POST" && request.Path == "/script")
                {
                    var scriptResult = RunOnMainThread(() => _session.Submit(request.Body ?? string.Empty));
                    if (scriptResult == null)
                    {
                        WriteJson(stream, 500, "{\"error\":\"timeout waiting for main thread\"}");
                        return;
                    }

                    WriteJson(stream, 200, JsonSerializer.Serialize(scriptResult));
                    return;
                }

                if (request.Method != "POST" || request.Path != "/exec")
                {
                    WriteJson(stream, 404, "{\"error\":\"not found\"}");
                    return;
                }

                var source = request.Body ?? string.Empty;
                var execResult = RunOnMainThread(() => CSharpExecutor.Execute(source));
                if (execResult == null)
                {
                    WriteJson(stream, 500, "{\"error\":\"timeout waiting for main thread\"}");
                    return;
                }

                WriteJson(stream, 200, JsonSerializer.Serialize(execResult));
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"Il2cppConsoleMod HTTP handler error: {ex}");
        }
    }

    private bool CheckAuth(HttpRequest request)
    {
        return request.Headers.TryGetValue("X-Debug-Token", out var value)
               && CryptographicOperations.FixedTimeEquals(
                   SHA256.HashData(Encoding.UTF8.GetBytes(value)),
                   _expectedTokenHash);
    }

    private CSharpExecutor.CSharpResult? RunOnMainThread(Func<CSharpExecutor.CSharpResult> action)
    {
        var tcs = new TaskCompletionSource<CSharpExecutor.CSharpResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        MainThreadDispatcher.Enqueue(() =>
        {
            var result = action();
            tcs.TrySetResult(result);
        });

        var completed = tcs.Task.Wait(TimeSpan.FromSeconds(60));
        return completed ? tcs.Task.Result : null;
    }

    private static HttpRequest? ReadHeaders(NetworkStream stream)
    {
        var headerBytes = new List<byte>();
        var one = new byte[1];

        while (headerBytes.Count < 64 * 1024)
        {
            var read = stream.Read(one, 0, 1);
            if (read == 0)
                return null;

            headerBytes.Add(one[0]);

            var n = headerBytes.Count;
            if (n >= 4 &&
                headerBytes[n - 4] == '\r' &&
                headerBytes[n - 3] == '\n' &&
                headerBytes[n - 2] == '\r' &&
                headerBytes[n - 1] == '\n')
            {
                break;
            }
        }

        var headerText = Encoding.UTF8.GetString(headerBytes.ToArray());
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0)
            return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line))
                continue;

            var idx = line.IndexOf(':');
            if (idx <= 0)
                continue;

            var name = line.Substring(0, idx).Trim();
            var value = line.Substring(idx + 1).Trim();
            headers[name] = value;
        }

        return new HttpRequest
        {
            Method = requestLine[0].ToUpperInvariant(),
            Path = requestLine[1],
            Headers = headers
        };
    }

    /// <summary>Reads the request body according to Content-Length. Returns false on malformed input.</summary>
    private static bool ReadBody(NetworkStream stream, HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Content-Length", out var lengthText)
            || !int.TryParse(lengthText, out var contentLength)
            || contentLength <= 0)
        {
            return true;
        }

        if (contentLength > MaxBodySize)
            return false;

        var bodyBytes = new byte[contentLength];
        var readTotal = 0;
        while (readTotal < contentLength)
        {
            var n = stream.Read(bodyBytes, readTotal, contentLength - readTotal);
            if (n == 0)
                return false;

            readTotal += n;
        }

        request.Body = Encoding.UTF8.GetString(bodyBytes);
        return true;
    }

    private static void WriteJson(NetworkStream stream, int statusCode, string json)
    {
        var reason = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "OK"
        };

        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var header = $"HTTP/1.1 {statusCode} {reason}\r\n" +
                     "Content-Type: application/json; charset=utf-8\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    private sealed class HttpRequest
    {
        public string Method { get; init; } = "";
        public string Path { get; init; } = "";
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Body { get; set; }
    }
}
