using Microsoft.AspNetCore.StaticFiles;
using System.Security.Cryptography;
using System.Text;

namespace Milvaion.Api.AppStartup;

/// <summary>
/// The SPA's static assets, with this deployment's base path substituted into them at startup.
///
/// Vite resolves asset URLs at build time, so the prefix the browser asks for - the <c>src</c> of every
/// script in index.html, the loader that fetches lazily routed chunks, the PWA manifest scope - is normally
/// frozen into the bundle by <c>vite build</c>. That forces one image per base path, and it makes
/// <c>VITE_BASE_PATH</c> and <c>MilvaionConfig:BasePath</c> two knobs that have to be kept equal by hand: set
/// them differently and the browser requests assets from a prefix the server does not publish, so the page
/// loads and then dies on 404s.
///
/// The frontend stage instead builds with <see cref="Placeholder"/> standing in for the real prefix. Here it
/// is replaced once, at boot, with whatever <c>MilvaionConfig:BasePath</c> says. The image becomes base-path
/// agnostic and the server-side value is the single source of truth, so the two can no longer disagree.
///
/// Rewritten files are held in memory rather than written back to wwwroot: the placeholder survives on disk,
/// so a restart with a different base path re-derives everything from the pristine build instead of trying to
/// substitute into an already-substituted file.
/// </summary>
internal sealed class RuntimeBasePathAssets
{
    /// <summary>
    /// The token the bundle is built with in place of a real base path. Must stay in step with the
    /// <c>VITE_BASE_PATH</c> value the Dockerfile's frontend stage builds with.
    /// </summary>
    public const string Placeholder = "/__MILVAION_BASE__";

    /// <summary>
    /// Only text assets can carry the placeholder. Images and fonts are skipped so boot does not read the
    /// whole of wwwroot into memory just to look for a string that cannot be in them.
    /// </summary>
    private static readonly string[] TextExtensions = [".html", ".js", ".css", ".webmanifest", ".json"];

    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    private readonly Dictionary<string, Asset> _assets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rewritten index.html, or <see langword="null"/> when wwwroot has no SPA build in it.
    /// The SPA fallback serves this so deep links get the same substituted document as "/" does.
    /// </summary>
    public Asset Index { get; private set; }

    public bool TryGet(PathString path, out Asset asset)
    {
        asset = null;

        return path.HasValue && _assets.TryGetValue(path.Value, out asset);
    }

    /// <summary>
    /// Reads wwwroot and returns the assets that mention the placeholder, rewritten for <paramref name="basePath"/>.
    /// </summary>
    public static RuntimeBasePathAssets Load(string webRoot, string basePath, ILogger logger)
    {
        var assets = new RuntimeBasePathAssets();

        if (string.IsNullOrWhiteSpace(webRoot) || !Directory.Exists(webRoot))
        {
            logger.LogWarning("wwwroot not found at '{WebRoot}'. The SPA will not be served.", webRoot);

            return assets;
        }

        // "" for root, "/milvaion" otherwise - never a trailing slash, because the placeholder is always
        // followed by one in the bundle ("/__MILVAION_BASE__/assets/..."), and root hosting has to collapse
        // to "/assets/..." rather than "//assets/...".
        var prefix = NormalizeBasePath(basePath);

        foreach (var file in Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories))
        {
            if (!TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;

            var requestPath = "/" + Path.GetRelativePath(webRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var isIndex = requestPath.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

            string content;

            try
            {
                content = File.ReadAllText(file);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not read '{File}' while applying the base path. Serving it unmodified.", requestPath);
                continue;
            }

            // index.html is always taken over, placeholder or not, because the SPA fallback has to serve it
            // from here; everything else is left to the static file middleware unless it needs substituting.
            if (!isIndex && !content.Contains(Placeholder, StringComparison.Ordinal))
                continue;

            var asset = Asset.Create(requestPath, content.Replace(Placeholder, prefix, StringComparison.Ordinal));

            assets._assets[requestPath] = asset;

            if (isIndex)
                assets.Index = asset;
        }

        logger.LogInformation("Base path '{BasePath}' applied to {Count} static asset(s) at startup.",
                              string.IsNullOrEmpty(prefix) ? "/" : prefix,
                              assets._assets.Count);

        return assets;
    }

    private static string NormalizeBasePath(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            return string.Empty;

        var trimmed = basePath.Trim().TrimEnd('/');

        if (trimmed.Length == 0)
            return string.Empty;

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    internal sealed class Asset
    {
        public byte[] Content { get; private init; }
        public string ContentType { get; private init; }

        /// <summary>
        /// Content hash of the rewritten bytes.
        ///
        /// The hash in an asset's filename describes what <c>vite build</c> produced, which is no longer what
        /// is served once the base path has been substituted in - so these responses cannot be marked
        /// immutable the way the untouched hashed assets are. Revalidation against this tag keeps them
        /// cacheable without letting a base path change go unnoticed by a browser holding an old copy.
        /// </summary>
        public string ETag { get; private init; }

        public static Asset Create(string requestPath, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);

            if (!ContentTypeProvider.TryGetContentType(requestPath, out var contentType))
                contentType = "application/octet-stream";

            return new Asset
            {
                Content = bytes,
                ContentType = contentType,
                ETag = $"\"{Convert.ToHexString(SHA256.HashData(bytes))[..32]}\""
            };
        }
    }
}
