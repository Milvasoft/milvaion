using Microsoft.EntityFrameworkCore;
using Milvaion.Api;
using Milvaion.Api.AppStartup;
using Milvaion.Api.Hubs;
using Milvaion.Api.Middlewares;
using Milvaion.Api.Migrations;
using Milvaion.Application;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.LinkedWithFormatters;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Domain;
using Milvaion.Infrastructure;
using Milvasoft.Components.Rest;
using Milvasoft.Core.Utils.Converters;
using Serilog;
using System.Reflection;

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        WebRootPath = GlobalConstant.WWWRoot
    });

    var assemblies = new Assembly[] { ApplicationAssembly.Assembly, InfrastructureAssembly.Assembly, DomainAssembly.Assembly, PresentationAssembly.Assembly };

    builder.AddObservibilityAndLogging();

    #region ConfigureServices

    // Add services to the container.
    var services = builder.Services;

    var fineConfig = builder.Configuration.GetSection(nameof(MilvaionConfig)).Get<MilvaionConfig>();

    services.AddSingleton(fineConfig);

    services.AddControllers().AddApplicationPart(PresentationAssembly.Assembly);

    services.AddEndpointsApiExplorer();

    services.AddVersioning();

    services.AddOpenApi(assemblies);

    services.AddAuthorization(builder.Configuration);

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

    app.UseCorsFromConfiguration(builder.Configuration);

    #region Configure

    // This must be first - BEFORE static files
    app.UseResponseCompression();

    // Prometheus metrics endpoint - /api/metrics
    app.UsePrometheusMetrics(builder.Configuration);

    // Serve static files (wwwroot) - Use Microsoft.AspNetCore.Builder extension directly
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

    app.UseScalarWithOpenApi();

    // SPA Fallback - Serve React app for all non-API routes
    // Must be LAST (after MapControllers, MapHub, etc.)
    app.MapFallbackToFile("index.html");

    #endregion

    await app.RunAsync();

}
catch (Exception ex)

{
    Log.Logger.Error(ex, "Error ");
    Console.WriteLine(ex.Message);
}

