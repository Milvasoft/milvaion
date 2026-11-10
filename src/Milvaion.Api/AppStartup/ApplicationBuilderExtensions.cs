using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Milvaion.Application.Utils.Extensions;
using Milvasoft.Core.MultiLanguage.Manager;
using Milvasoft.Localization;
using Scalar.AspNetCore;
using Serilog;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;

namespace Milvaion.Api.AppStartup;

/// <summary>
/// Application builder and service collection extensions.
/// </summary>
public static partial class StartupExtensions
{
    /// <summary>
    /// Maps the OpenAPI document and the Scalar reference when <c>MilvaionConfig:Developer:ApiDocsEnabled</c> is set.
    /// </summary>
    /// <remarks>
    /// Callers authorize in the reference with their own token. Never embed one here: this page is anonymous, so
    /// anything written into it is handed to whoever opens it.
    /// </remarks>
    /// <param name="app"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseScalarWithOpenApi(this WebApplication app, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("MilvaionConfig:Developer:ApiDocsEnabled"))
            return app;

        app.MapOpenApi(GlobalConstant.RoutePrefix + "/docs/{documentName}/docs.json");

        app.MapScalarApiReference(endpointPrefix: $"/{GlobalConstant.RoutePrefix}/documentation", options =>
        {
            options.WithOpenApiRoutePattern($"/{GlobalConstant.RoutePrefix}/docs/v1.0/docs.json");
            options.WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Axios);

            //UI
            options.WithTitle("Milvaion Api Reference")
                       .WithFavicon("https://demo.milvasoft.com/api/favicon.ico")
                       .WithDocumentDownloadType(DocumentDownloadType.None)
                       .EnableDarkMode()
                       .AddPreferredSecuritySchemes(JwtBearerDefaults.AuthenticationScheme)
                       .WithCustomCss(".darklight-reference-promo { display: none !important; } .darklight-reference { padding-bottom: 15px !important; } .open-api-client-button { display: none !important; }");
        });

        return app;
    }

    /// <summary>
    /// Adds the required middleware to use the localization. Configures the options before add..
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseRequestLocalization(this WebApplication app)
    {
        var supportedCultures = LanguagesSeed.Seed.Select(i => new CultureInfo(i.Code)).ToArray();

        var defaultLanguageCode = LanguagesSeed.Seed.First(l => l.IsDefault).Code;

        var options = new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(defaultLanguageCode),
            SupportedCultures = supportedCultures,
            SupportedUICultures = supportedCultures,
            ApplyCurrentCultureToResponseHeaders = true
        };

        var defaultCulture = new CultureInfo(defaultLanguageCode);

        CultureInfo.CurrentCulture = defaultCulture;
        CultureInfo.CurrentUICulture = defaultCulture;
        CultureInfo.DefaultThreadCurrentCulture = defaultCulture;

        _ = new CultureSwitcher(defaultLanguageCode);

        return app.UseRequestLocalization(options);
    }

    /// <summary>
    /// Adds the required middleware to serve static files. Configures the options before adding.
    /// </summary>
    /// <param name="app">The <see cref="IApplicationBuilder"/> instance.</param>
    /// <returns>The <see cref="IApplicationBuilder"/> instance.</returns>
    public static IApplicationBuilder UseStaticFiles(this WebApplication app)
    {
        app.UseStaticFiles($"/{GlobalConstant.RoutePrefix}");

        return app;
    }

    /// <summary>
    /// Uses CORS configuration from appsettings.
    /// </summary>
    /// <param name="app"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static IApplicationBuilder UseCorsFromConfiguration(this IApplicationBuilder app, IConfiguration configuration)
    {
        var corsConfig = configuration.GetSection("Cors").Get<CorsOptionsConfig>() ?? throw new InvalidOperationException("Cors configuration missing");

        if (string.IsNullOrWhiteSpace(corsConfig.DefaultPolicy))
            throw new InvalidOperationException("Cors DefaultPolicy not defined");

        app.UseCors(corsConfig.DefaultPolicy);

        return app;
    }

    /// <summary>
    /// Maps the Prometheus scraping endpoint for metrics.
    /// Call this after building the app: app.UseOpenTelemetryPrometheusScrapingEndpoint();
    /// </summary>
    /// <param name="app"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static IApplicationBuilder UsePrometheusMetrics(this WebApplication app, IConfigurationManager configuration)
    {
        var exportPath = configuration.GetSection("MilvaionConfig:OpenTelemetry:ExportPath").Get<string>();

        app.UseOpenTelemetryPrometheusScrapingEndpoint(exportPath);
        Log.Logger.Information("Prometheus metrics endpoint enabled at {Path}", exportPath);
        return app;
    }

    /// <summary>
    /// Name of the rate limiting policy applied to the login and token refresh endpoints.
    /// </summary>
    public const string LoginRateLimitPolicy = "login";

    /// <summary>
    /// Configuration path of the local token signing key.
    /// </summary>
    public const string JwtSigningKeyPath = "Milvasoft:Identity:Token:SymmetricPublicKey";

    /// <summary>
    /// Configuration path of the api key signing secret.
    /// </summary>
    public const string ApiKeySecretPath = "MilvaionConfig:ApiKey:Secret";

    /// <summary>
    /// Prefix of the placeholder values tracked in appsettings. A value starting with it counts as not configured.
    /// </summary>
    public const string SecretPlaceholderPrefix = "CHANGE_ME";

    /// <summary>
    /// Whether the developer endpoints (database reset, seed, export...) are registered at all.
    /// </summary>
    public static bool IsDeveloperEndpointsEnabled(this IConfiguration configuration) => configuration.GetValue<bool>("MilvaionConfig:Developer:EndpointsEnabled");

    /// <summary>
    /// Replaces unset signing secrets with random values held only by this process.
    /// </summary>
    /// <remarks>
    /// The signing keys used to be committed, so an unset value must never fall back to a value somebody else can
    /// know. Refusing to start would take down a deployment that has not set them yet, so it runs degraded instead:
    /// sessions and api keys do not survive a restart until the variables are set.
    /// </remarks>
    /// <param name="configuration"></param>
    /// <returns>The configuration paths that had to be generated.</returns>
    public static List<string> EnsureSigningSecrets(this IConfigurationManager configuration)
    {
        var generated = new Dictionary<string, string>();

        // The api key secret is read as ASCII, so both values stay in the hex alphabet.
        foreach (var path in new[] { JwtSigningKeyPath, ApiKeySecretPath })
            if (IsUnsetSecret(configuration[path]))
                generated[path] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        if (generated.Count > 0)
            configuration.AddInMemoryCollection(generated);

        return [.. generated.Keys];
    }

    /// <summary>
    /// Whether the value is empty or still the tracked placeholder.
    /// </summary>
    public static bool IsUnsetSecret(string value) => string.IsNullOrWhiteSpace(value) || value.StartsWith(SecretPlaceholderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Trusts forwarded headers only from the proxies listed under <c>MilvaionConfig:ForwardedHeaders</c>.
    /// Without any listed proxy the headers are ignored, because any client can send them.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static IServiceCollection AddForwardedHeadersFromConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        var proxies = configuration.GetSection("MilvaionConfig:ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        var networks = configuration.GetSection("MilvaionConfig:ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];

        return services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in proxies)
                options.KnownProxies.Add(IPAddress.Parse(proxy));

            foreach (var network in networks)
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        });
    }

    /// <summary>
    /// Adds the forwarded headers middleware when at least one proxy is trusted.
    /// </summary>
    /// <param name="app"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseForwardedHeadersFromConfiguration(this IApplicationBuilder app, IConfiguration configuration)
    {
        var section = configuration.GetSection("MilvaionConfig:ForwardedHeaders");

        return section.GetSection("KnownProxies").GetChildren().Any() || section.GetSection("KnownNetworks").GetChildren().Any()
            ? app.UseForwardedHeaders()
            : app;
    }

    /// <summary>
    /// Registers the per client address limit for login and token refresh.
    /// <c>MilvaionConfig:RateLimiting:LoginPermitPerMinute</c> sets the limit, 0 turns it off.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static IServiceCollection AddLoginRateLimiter(this IServiceCollection services, IConfiguration configuration)
    {
        var permitPerMinute = configuration.GetValue("MilvaionConfig:RateLimiting:LoginPermitPerMinute", 60);

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(LoginRateLimitPolicy, context => permitPerMinute <= 0
                ? RateLimitPartition.GetNoLimiter("disabled")
                : RateLimitPartition.GetFixedWindowLimiter(context.GetIpAddress() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        });
    }
}
