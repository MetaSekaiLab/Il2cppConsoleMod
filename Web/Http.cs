using System.Text;

namespace Il2cppConsoleMod.Web;

internal sealed class HttpRequest
{
    public string Method { get; init; } = "";
    /// <summary>Raw request target, query string included.</summary>
    public string Path { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Body { get; set; }

    /// <summary>Path without the query string.</summary>
    public string Route
    {
        get
        {
            var q = Path.IndexOf('?');
            return q >= 0 ? Path.Substring(0, q) : Path;
        }
    }

    public Dictionary<string, string> Query
    {
        get
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var q = Path.IndexOf('?');
            if (q < 0)
                return result;

            foreach (var pair in Path.Substring(q + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                result[key] = value;
            }

            return result;
        }
    }
}

internal static class HttpResponse
{
    public static void Json(Stream stream, int statusCode, string json)
        => Write(stream, statusCode, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

    public static void Write(Stream stream, int statusCode, string contentType, byte[] body, IEnumerable<string>? extraHeaders = null)
    {
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(Reason(statusCode)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n");

        if (extraHeaders != null)
        {
            foreach (var line in extraHeaders)
                header.Append(line).Append("\r\n");
        }

        header.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static string Reason(int statusCode) => statusCode switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        410 => "Gone",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "OK"
    };
}
