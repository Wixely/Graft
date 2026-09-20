using System.Reflection;
using System.Security.Cryptography;

namespace Graft.Server;

public sealed class WebAssets
{
    private sealed record Asset(string Resource, string Etag, long Length);
    private readonly Assembly assembly = typeof(WebAssets).Assembly;
    private readonly Dictionary<string, Asset> assets = new(StringComparer.Ordinal);
    public int Count => assets.Count;
    public long Bytes => assets.Values.Sum(asset => asset.Length);

    public WebAssets()
    {
        foreach (string resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
        {
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            assets.Add("/" + resource[4..].Replace('\\', '/'),
                new(resource, '"' + Convert.ToHexStringLower(SHA256.HashData(stream)) + '"', stream.Length));
        }
        if (!assets.ContainsKey("/index.html")) throw new InvalidOperationException("Published client assets were not embedded.");
    }

    public async Task Serve(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        string path = context.Request.Path.Value ?? "/";
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.Equals("/api", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(".git", StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (path == "/" || !Path.HasExtension(path)) path = "/index.html";
        if (path.EndsWith(".br", StringComparison.Ordinal) || path.EndsWith(".gz", StringComparison.Ordinal) ||
            !assets.TryGetValue(path, out Asset? asset))
        { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        IList<Microsoft.Net.Http.Headers.StringWithQualityHeaderValue>? encodings = context.Request.GetTypedHeaders().AcceptEncoding;
        double Quality(string name)
        {
            Microsoft.Net.Http.Headers.StringWithQualityHeaderValue? match = encodings?
                .FirstOrDefault(value => value.Value.Equals(name, StringComparison.OrdinalIgnoreCase));
            return match is null ? 0 : match.Quality ?? 1;
        }
        string? selected = null;
        if (Quality("br") > 0 && assets.TryGetValue(path + ".br", out Asset? compressed)) { asset = compressed; selected = "br"; }
        else if (Quality("gzip") > 0 && assets.TryGetValue(path + ".gz", out compressed)) { asset = compressed; selected = "gzip"; }
        context.Response.Headers.Vary = "Accept-Encoding";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = asset.Etag;
        context.Response.ContentType = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" => "application/json",
            ".wasm" => "application/wasm",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };
        if (selected is not null) context.Response.Headers.ContentEncoding = selected;
        if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(value => value.Tag.Value == asset.Etag || value.Tag.Value == "*") == true)
        { context.Response.StatusCode = StatusCodes.Status304NotModified; return; }
        context.Response.ContentLength = asset.Length;
        if (HttpMethods.IsHead(context.Request.Method)) return;
        using Stream source = assembly.GetManifestResourceStream(asset.Resource)!;
        await source.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
