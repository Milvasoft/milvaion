using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain.Enums;
using Milvasoft.Core.MultiLanguage.Manager;
using Milvasoft.Identity.Concrete.Options;
using Milvasoft.Identity.TokenProvider.AuthToken;
using Milvasoft.Localization;
using Scalar.AspNetCore;
using Serilog;
using System.Globalization;
using System.Security.Claims;

namespace Milvaion.Api.AppStartup;

/// <summary>
/// Application builder and service collection extensions.
/// </summary>
public static partial class StartupExtensions
{
    /// <summary>
    /// Adds the required middleware to use the localization. Configures the options before add..
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseScalarWithOpenApi(this WebApplication app)
    {
        if (MilvaionExtensions.IsCurrentEnvProduction())
            return app;

        app.MapOpenApi(GlobalConstant.RoutePrefix + "/docs/{documentName}/docs.json");

        app.MapScalarApiReference(endpointPrefix: $"/{GlobalConstant.RoutePrefix}/documentation", options =>
        {
            options.WithOpenApiRoutePattern($"/{GlobalConstant.RoutePrefix}/docs/v1.0/docs.json");
            options.WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Axios);
            options.AddHttpAuthentication("Bearer", auth =>
            {
                auth.Token = GenerateTokenForUI(app);
            });

            //UI
            options.WithTitle("Milvaion Api Reference")
                       .WithFavicon("https://demo.milvasoft.com/api/favicon.ico")
                       .WithDocumentDownloadType(DocumentDownloadType.None)
                       .EnableDarkMode()
                       .AddPreferredSecuritySchemes(JwtBearerDefaults.AuthenticationScheme)
                       .WithCustomCss(".darklight-reference-promo { display: none !important; } .darklight-reference { padding-bottom: 15px !important; } .open-api-client-button { display: none !important; }");
        });

        return app;

        static string GenerateTokenForUI(WebApplication app)
        {
            var identityOptions = app.Services.GetRequiredService<MilvaIdentityOptions>();

            var tokenManager = new MilvaTokenManager(identityOptions, null);

            var roleClaim = new Claim(ClaimTypes.Role, PermissionCatalog.App.SuperAdmin);
            var userTypeClaim = new Claim(GlobalConstant.UserTypeClaimName, UserType.Manager.ToString());
            var userClaim = new Claim(ClaimTypes.Name, "rootuser");

            var accessToken = tokenManager.GenerateToken(expired: DateTime.UtcNow.AddYears(1), issuer: null, userClaim, roleClaim, userTypeClaim);

            return accessToken;
        }
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
}
