using System.Reflection;
using System.Security.Cryptography;

namespace Graft.Ui.Server;

public sealed class WebAssets
{
    private sealed record Asset(string Resource, string Etag, long Length);
    private readonly Assembly assembly = typeof(WebAssets).Assembly;
    private readonly Dictionary<string, Asset> assets = new(StringComparer.Ordinal);
    public int Count => assets.Count;
    public long Bytes => assets.Values.Sum(a => a.Length);

    public WebAssets()
    {
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("web/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            assets.Add("/" + resource[4..].Replace('\\', '/'), new Asset(resource, '"' + Convert.ToHexStringLower(SHA256.HashData(stream)) + '"', stream.Length));
        }
        if (!assets.ContainsKey("/index.html")) throw new InvalidOperationException("Published client assets were not embedded.");
    }

    public async Task Serve(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) { context.Response.StatusCode = 405; return; }
        var path = context.Request.Path.Value ?? "/";
        // Explicit UI routes only. Never return HTML for an API, Git or missing asset URL.
        if (path is "/" or "/projects" or "/projects/") path = "/index.html";
        if (path.EndsWith(".br", StringComparison.Ordinal) || path.EndsWith(".gz", StringComparison.Ordinal) || !assets.TryGetValue(path, out var asset))
        { context.Response.StatusCode = 404; return; }

        var encoding = context.Request.GetTypedHeaders().AcceptEncoding;
        double? ExplicitQuality(string name)
        {
            var entry = encoding?.FirstOrDefault(e => e.Value.Equals(name, StringComparison.OrdinalIgnoreCase));
            return entry is null ? null : entry.Quality ?? 1;
        }
        double Quality(string name) => ExplicitQuality(name) ?? ExplicitQuality("*") ?? 0;
        var br = Quality("br"); var gzip = Quality("gzip");
        string? selected = null;
        if (br > 0 && br >= gzip && assets.TryGetValue(path + ".br", out var compressed)) { asset = compressed; selected = "br"; }
        else if (gzip > 0 && assets.TryGetValue(path + ".gz", out compressed)) { asset = compressed; selected = "gzip"; }
        else if (br > 0 && assets.TryGetValue(path + ".br", out compressed)) { asset = compressed; selected = "br"; }
        if (selected is null && (ExplicitQuality("identity") ?? (ExplicitQuality("*") == 0 ? 0 : 1)) == 0)
        { context.Response.StatusCode = 406; return; }
        context.Response.Headers.Vary = "Accept-Encoding";
        context.Response.Headers.CacheControl = "no-cache"; // Revalidate even fingerprinted assets in this experiment.
        context.Response.Headers.ETag = asset.Etag;
        context.Response.ContentType = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8", ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" => "application/json", ".wasm" => "application/wasm", _ => "application/octet-stream"
        };
        if (selected is not null) context.Response.Headers.ContentEncoding = selected;
        if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(e => e.Tag.Value == asset.Etag || e.Tag.Value == "*") == true)
        { context.Response.StatusCode = 304; return; }
        context.Response.ContentLength = asset.Length;
        if (HttpMethods.IsHead(context.Request.Method)) return;
        using var source = assembly.GetManifestResourceStream(asset.Resource)!;
        await source.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
