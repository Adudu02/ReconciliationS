using Microsoft.Extensions.FileProviders;

namespace Reconciliation.Api;

internal static class WebShell
{
    private static readonly string[] ReservedPrefixes = ["/companies", "/auth", "/openapi.json", "/health", "/test", "/_framework"];
    private const string Policy = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    internal static void UseWebShell(this WebApplication app)
    {
        // Source assets also make the shell testable without publishing a WASM runtime during API tests.
        var root = app.Environment.WebRootPath;
        if (root is null || !File.Exists(Path.Combine(root, "index.html")))
            root = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../Reconciliation.Web/wwwroot"));
        var files = new PhysicalFileProvider(root);
        app.Lifetime.ApplicationStopped.Register(files.Dispose);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context => SetHeaders(context.Context, context.File.Name)
        });
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var reserved = ReservedPrefixes.Any(prefix => path.StartsWithSegments(prefix));
            if (context.GetEndpoint() is not null || reserved) { await next(context); return; }
            var html = context.Request.GetTypedHeaders().Accept?.Any(value => value.MediaType == "text/html" && value.Quality != 0) == true;
            if (HttpMethods.IsGet(context.Request.Method) && html && !Path.HasExtension(path))
            {
                SetHeaders(context, "index.html");
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(files.GetFileInfo("index.html"), context.RequestAborted);
            }
            else context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
    }

    private static void SetHeaders(HttpContext context, string name)
    {
        context.Response.Headers.ContentSecurityPolicy = Policy;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        // Only fingerprinted framework filenames can safely be immutable across deployments.
        var fingerprinted = context.Request.Path.StartsWithSegments("/_framework") && name.Split('.').Length >= 3
            && name.Split('.')[^2] is { Length: 10 } fingerprint && fingerprint.All(char.IsAsciiLetterOrDigit);
        context.Response.Headers.CacheControl = fingerprinted ? "public,max-age=31536000,immutable" : "no-cache";
    }
}
