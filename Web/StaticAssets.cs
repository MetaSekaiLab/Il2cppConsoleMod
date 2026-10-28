namespace Il2cppConsoleMod.Web;

/// <summary>The web UI, embedded into the DLL at build time and served without a token (it holds no secrets).</summary>
internal static class StaticAssets
{
    private const string Prefix = "Il2cppConsoleMod.wwwroot.";

    // The page only talks to this origin; inline scripts and framing are refused.
    private const string ContentSecurityPolicy =
        "Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; connect-src 'self'; font-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly Dictionary<string, (string file, string type)> Routes = new(StringComparer.Ordinal)
    {
        ["/"] = ("index.html", "text/html; charset=utf-8"),
        ["/index.html"] = ("index.html", "text/html; charset=utf-8"),
        ["/app.js"] = ("app.js", "text/javascript; charset=utf-8"),
        ["/app.css"] = ("app.css", "text/css; charset=utf-8"),
        ["/favicon.svg"] = ("favicon.svg", "image/svg+xml"),
    };

    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    public static bool TryServe(HttpRequest request, Stream stream)
    {
        if (request.Method != "GET" || !Routes.TryGetValue(request.Route, out var asset))
            return false;

        var bytes = Load(asset.file);
        if (bytes == null)
            return false;

        var headers = asset.file.EndsWith(".html", StringComparison.Ordinal)
            ? new[] { ContentSecurityPolicy, "Referrer-Policy: no-referrer", "X-Frame-Options: DENY" }
            : null;
        HttpResponse.Write(stream, 200, asset.type, bytes, headers);
        return true;
    }

    private static byte[]? Load(string file)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var cached))
                return cached;
        }

        using var resource = typeof(StaticAssets).Assembly.GetManifestResourceStream(Prefix + file);
        if (resource == null)
            return null;

        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var bytes = memory.ToArray();
        lock (Cache)
            Cache[file] = bytes;
        return bytes;
    }
}
