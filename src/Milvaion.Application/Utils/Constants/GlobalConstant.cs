using System.Diagnostics;

namespace Milvaion.Application.Utils.Constants;

/// <summary>
/// Represents a class that contains global constants for the application.
/// </summary>
public static class GlobalConstant
{
    #region Path

    /// <summary>
    /// Route prefix of api.
    /// </summary>
    public const string RoutePrefix = "api";

    /// <summary>
    /// Base route path of api.
    /// </summary>
    public const string RouteBase = RoutePrefix + "/" + "v{version:apiVersion}";

    /// <summary>
    /// Full route path of api. It includes <see cref="RouteBase"/> and controller name.
    /// </summary>
    public const string FullRoute = $"{RouteBase}/[controller]";

    /// <summary>
    /// Root directory path.
    /// </summary>
    public const string WWWRoot = "wwwroot";

    /// <summary>
    /// http string.
    /// </summary>
    public const string Http = "http";

    /// <summary>
    /// https string.
    /// </summary>
    public const string Https = "https";

    /// <summary>
    /// Localization resources folder name.
    /// </summary>
    public const string LocalizationResourcesFolderName = "LocalizationResources";

    /// <summary>
    /// Resources folder name.
    /// </summary>
    public const string ResourcesFolderName = "Resources";

    #endregion

    /// <summary>
    /// Rootpath of application.
    /// </summary>
    public static string RootPath { get; } = Environment.CurrentDirectory;

    /// <summary>
    /// SQL folder path.
    /// </summary>
    public static string SqlFilesPath { get; } = Path.Combine(RootPath, "StaticFiles", "SQL");

    /// <summary>
    /// SQL folder path.
    /// </summary>
    public static string JsonFilesPath { get; } = Path.Combine(RootPath, "StaticFiles", "JSON");

    /// <summary>
    /// Default api version.
    /// </summary>
    public const string DefaultApiVersion = "v1.0";

    /// <summary>
    /// Default api version.
    /// </summary>
    public const string CurrentApiVersion = "1.0";

    /// <summary>
    /// Custom forbid scheme for super admin.
    /// </summary>
    public const string CustomForbidSchema = "CustomForbidSchema";

    /// <summary>
    /// Logging activity name for activity starter interception.
    /// </summary>
    public const string LoggingActivityName = "LoggingActivity";

    /// <summary>
    /// Gets or sets the current environment.
    /// </summary>
    public static string CurrentEnvironment { get; set; }

    /// <summary>
    /// UserType claim name.
    /// </summary>
    public const string UserTypeClaimName = "ut";

    /// <summary>
    /// Response logging ignore httpcontext items key.
    /// </summary>
    public const string IgnoreResponseLoggingItemsKey = "IgnoreResponseLogging";

    /// <summary>
    /// Root user name.
    /// </summary>
    public const string RootUsername = "rootuser";

    /// <summary>
    /// System user name.
    /// </summary>
    public const string SystemUsername = "System";

    /// <summary>
    /// Generate metadata header key.
    /// </summary>
    public const string GenerateMetadataHeaderKey = "M-Metadata";

    /// <summary>
    /// Generate metadata header key.
    /// </summary>
    public const string ContentDispositionHeaderKey = "Content-Disposition";

    /// <summary>
    /// X-Real-IP header key.
    /// </summary>
    public const string RealIpHeaderKey = "X-Real-IP";

    /// <summary>
    /// Generate metadata header key.
    /// </summary>
    public const string DownloadEnpointPathEnd = "/download";

    /// <summary>
    /// Default ip.
    /// </summary>
    public const string DefaultIp = "0.0.0.1";

    /// <summary>
    /// Url start segment.
    /// </summary>
    public const string UrlStartSegment = "://";

    /// <summary>
    /// Current user permissions http context items key.
    /// </summary>
    public const string CurrentUserPermissionsKey = "CU-Permissions";

    /// <summary>
    /// Auto increment start value for database entities.
    /// </summary>
    public const int AutoIncrementStart = 21;

    /// <summary>
    /// Ignored content disposition parts.
    /// </summary>
    public static HashSet<string> ContentDispositionIgnores { get; } =
    [
        "attachment",
        "inline"
    ];

    /// <summary>
    /// UI request paths.
    /// </summary>
    public static HashSet<string> UIPaths { get; } =
    [
        $"/{RoutePrefix}/documentation",
        $"/{RoutePrefix}/docs"
    ];

    /// <summary>
    /// UI request paths.
    /// </summary>
    public static HashSet<string> IgnoringLogPaths { get; } =
    [
        $"healthcheck",
        $"well-known",
    ];

    /// <summary>
    /// Activity source.
    /// </summary>
    public static ActivitySource ActivitySource { get; } = new("milvaion-api");
}

