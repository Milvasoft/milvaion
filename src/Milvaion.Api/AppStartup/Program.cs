using Microsoft.Extensions.Options;
using Milvaion.Api;
using Milvaion.Api.AppStartup;
using Milvaion.Api.Hubs;
using Milvaion.Api.Middlewares;
using Milvaion.Api.Migrations;
using Milvaion.Application;
using Milvaion.Application.Utils.LinkedWithFormatters;
using Milvaion.Infrastructure;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvasoft.Components.Rest;
using Milvasoft.Core.Utils.Converters;
using Serilog;
using System.Reflection;

try
{
    MissingResxKeyFinder.FindAndPrintToConsole();

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        WebRootPath = GlobalConstant.WWWRoot
    });

    var assemblies = new Assembly[] { ApplicationAssembly.Assembly, InfrastructureAssembly.Assembly, DomainAssembly.Assembly, PresentationAssembly.Assembly };

    builder.AddObservibilityAndLogging();

    #region ConfigureServices

    // Add services to the container.
    var services = builder.Services;

    var config = builder.Configuration.GetSection(nameof(MilvaionConfig)).Get<MilvaionConfig>();

    services.AddSingleton(config);

    services.AddControllers().AddApplicationPart(PresentationAssembly.Assembly);

    services.AddEndpointsApiExplorer();

    services.AddVersioning();

    services.AddOpenApi(assemblies);

    services.AddAuthorization(builder.Configuration);

    services.AddMilvaionMcp();

    services.AddHttpContextAccessor();

    services.AddMultiLanguageSupport(builder.Configuration);

    services.ConfigureCurrentMilvaJsonSerializerOptions().AddResponseConverters();

    services.AddAndConfigureResponseCompression();

    services.AddApplicationServices(assemblies);

    services.AddHostedService<MigrationHostedService>();

    services.AddInfrastructureServices(builder.Configuration);

    // Add SignalR
    services.AddSignalR();

    // Register SignalR event publisher
    services.AddScoped<Milvaion.Application.Interfaces.IJobOccurrenceEventPublisher, Milvaion.Api.Services.SignalRJobOccurrenceEventPublisher>();

    services.AddLinkedWithFormatters(assemblies);

    services.AddFetchers();

    #endregion

    services.AddCorsFromConfiguration(builder.Configuration);

    services.Configure<RouteOptions>(options =>
    {
        options.LowercaseUrls = true;
        options.LowercaseQueryStrings = true;
    });

    // Configure the HTTP request pipeline.
    var app = builder.Build();

    // PathBase support: when BasePath is configured (e.g. "/milvaion"), all middleware and endpoints (controllers, hubs, static files, SPA fallback) are automatically scoped to it.
    // UseRouting() MUST be called explicitly right after UsePathBase so that route matching happens on the already-stripped path, not the full original path.
    if (!string.IsNullOrWhiteSpace(config?.BasePath))
    {
        app.UsePathBase(config.BasePath);
        app.UseRouting();
    }

    var effectiveBasePath = string.IsNullOrWhiteSpace(config?.BasePath) ? "/" : config.BasePath;
    var publicBasePath = config?.EffectivePublicBasePath;

    if (app.Logger.IsEnabled(LogLevel.Information))
        app.Logger.LogInformation("Milvaion starting on base path: {BasePath} (public: {PublicBasePath})",
                                  effectiveBasePath,
                                  string.IsNullOrWhiteSpace(publicBasePath) ? "/" : publicBasePath);

    app.UseCorsFromConfiguration(builder.Configuration);

    // SPA assets are built with a placeholder in place of the base path and substituted here, so one image can
    // be served under any prefix. The public value is the one the browser resolves against - behind a
    // prefix-stripping proxy that is not the same as the path base this application listens on.
    // See RuntimeBasePathAssets.
    var spaAssets = app.LoadRuntimeBasePathAssets(publicBasePath);

    #region Configure

    // This must be first - BEFORE static files
    app.UseResponseCompression();

    // Prometheus metrics endpoint - /api/metrics
    app.UsePrometheusMetrics(builder.Configuration);

    // Must come before UseStaticFiles, which would otherwise serve the un-substituted file from disk.
    app.UseRuntimeBasePath(spaAssets);

    // Serve static files (wwwroot)
    StaticFileExtensions.UseStaticFiles(app, new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            // Cache static assets for 1 year (immutable hashed filenames)
            if (ctx.File.Name.Contains('-') && (ctx.File.Name.EndsWith(".js") || ctx.File.Name.EndsWith(".css")))
            {
                ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
            }
        }
    });

    app.UseRequestLocalization();

    app.UseMiddleware<ExceptionMiddleware>();

    app.UseAuthentication();

    app.UseAuthorization();

    app.MapControllers();

    // Map SignalR hub
    app.MapHub<JobsHub>("/hubs/jobs");

    // Must come before MapFallbackToFile, otherwise the SPA fallback would swallow /mcp.
    app.MapMilvaionMcp();

    app.UseScalarWithOpenApi();

    // SPA Fallback - Serve React app for all non-API routes
    // Must be LAST (after MapControllers, MapHub, etc.)
    app.MapRuntimeBasePathFallback(spaAssets);

    // Runtime config endpoint — serves base path and other boot-time settings to the SPA.
    // Must be reachable BEFORE authentication so the browser can load it as a plain <script>.
    // Cache-Control: no-store prevents PWA/browser from caching stale config across deployments.
    app.MapGet("/config.js", (IOptions<MilvaionConfig> opts, HttpContext ctx) =>
    {
        // The public prefix, not the path base: this drives the router basename and the base URL for API and
        // SignalR calls, all of which the browser resolves on the far side of any reverse proxy.
        var basePath = (opts.Value.EffectivePublicBasePath ?? string.Empty).TrimEnd('/');
        var js = $"window.__MILVAION_CONFIG__ = {{ basePath: '{basePath}' }};";

        ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        return Results.Content(js, "application/javascript", System.Text.Encoding.UTF8);
    })
    .ExcludeFromDescription()
    .WithMetadata(new Microsoft.AspNetCore.Routing.RouteNameMetadata("runtime-config"))
    .AllowAnonymous();

    #endregion

    // Initialize RabbitMQ queues and exchanges before starting the application
    var rabbitMQFactory = app.Services.GetRequiredService<RabbitMQConnectionFactory>();

    await rabbitMQFactory.InitializeAsync();

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Logger.Error(ex, "Error ");
    Console.WriteLine(ex.Message);
}

