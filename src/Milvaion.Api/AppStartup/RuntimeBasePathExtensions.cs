namespace Milvaion.Api.AppStartup;

/// <summary>
/// Wires <see cref="RuntimeBasePathAssets"/> into the request pipeline.
/// </summary>
internal static class RuntimeBasePathExtensions
{
    /// <summary>
    /// Reads wwwroot once and substitutes <paramref name="basePath"/> into the SPA assets that carry the
    /// build-time placeholder. The result is handed to <see cref="UseRuntimeBasePath"/> and
    /// <see cref="MapRuntimeBasePathFallback"/>, which both serve out of it.
    /// </summary>
    public static RuntimeBasePathAssets LoadRuntimeBasePathAssets(this WebApplication app, string basePath)
    {
        WarnOnStaleBuildTimeBasePath(app.Logger, basePath);

        return RuntimeBasePathAssets.Load(app.Environment.WebRootPath, basePath, app.Logger);
    }

    /// <summary>
    /// Serves the substituted assets ahead of the static file middleware.
    ///
    /// Must be registered after <c>UsePathBase</c> - by then <c>Request.Path</c> has had the prefix stripped,
    /// so it lines up with the wwwroot-relative paths the assets are keyed by - and before
    /// <c>UseStaticFiles</c>, which would otherwise serve the pristine file with the placeholder still in it.
    /// </summary>
    public static WebApplication UseRuntimeBasePath(this WebApplication app, RuntimeBasePathAssets assets)
    {
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                await next(context);
                return;
            }

            if (!assets.TryGet(context.Request.Path, out var asset))
            {
                await next(context);
                return;
            }

            await WriteAssetAsync(context, asset);
        });

        return app;
    }

    /// <summary>
    /// Serves the substituted index.html for SPA routes.
    ///
    /// Keeps the <c>nonfile</c> constraint that <c>MapFallbackToFile</c> applies, so a request for an asset
    /// that genuinely is not there still 404s instead of being answered with an HTML document the browser
    /// then refuses to execute as a script.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "ASP0018:Unused route parameter", Justification = "<Pending>")]
    public static WebApplication MapRuntimeBasePathFallback(this WebApplication app, RuntimeBasePathAssets assets)
    {
        if (assets.Index is null)
        {
            app.MapFallbackToFile("index.html");

            return app;
        }

        app.MapFallback("{*path:nonfile}", async context => await WriteAssetAsync(context, assets.Index));

        return app;
    }

    private static async Task WriteAssetAsync(HttpContext context, RuntimeBasePathAssets.Asset asset)
    {
        var response = context.Response;

        response.Headers.ETag = asset.ETag;

        // Not immutable: the bytes depend on the base path, which a redeploy can change without changing the
        // hash in the filename. Revalidation is cheap - a matching ETag costs a 304 and no body.
        response.Headers.CacheControl = "no-cache";

        if (context.Request.Headers.IfNoneMatch.Contains(asset.ETag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.ContentType = asset.ContentType;
        response.ContentLength = asset.Content.Length;

        if (HttpMethods.IsHead(context.Request.Method))
            return;

        await response.Body.WriteAsync(asset.Content);
    }

    /// <summary>
    /// Points out a <c>VITE_BASE_PATH</c> left in the environment that disagrees with the configured base path.
    ///
    /// It is no longer read at runtime - the image is built with a placeholder and the server decides - but it
    /// used to be the knob for this, so an operator setting it and seeing nothing change deserves to be told
    /// which value actually won rather than left to work it out from 404s.
    /// </summary>
    private static void WarnOnStaleBuildTimeBasePath(ILogger logger, string basePath)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("VITE_BASE_PATH");

        if (string.IsNullOrWhiteSpace(fromEnvironment))
            return;

        var normalizedEnvironment = fromEnvironment.Trim().TrimEnd('/');
        var normalizedConfig = (basePath ?? string.Empty).Trim().TrimEnd('/');

        if (string.Equals(normalizedEnvironment, normalizedConfig, StringComparison.OrdinalIgnoreCase))
            return;

        logger.LogWarning("VITE_BASE_PATH is set to '{ViteBasePath}' but the prefix served to the browser is " +
                          "'{PublicBasePath}', taken from MilvaionConfig:PublicBasePath (falling back to " +
                          "MilvaionConfig:BasePath). VITE_BASE_PATH is only read when building the frontend " +
                          "and is ignored here.",
                          fromEnvironment,
                          string.IsNullOrEmpty(normalizedConfig) ? "/" : normalizedConfig);
    }
}
