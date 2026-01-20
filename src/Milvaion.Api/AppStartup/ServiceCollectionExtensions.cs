using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Milvaion.Api.Controllers;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Domain;
using Milvaion.Infrastructure.Logging;
using Milvaion.Infrastructure.Utils.OpenApi;
using Milvasoft.Components.OpenApi;
using Milvasoft.Core.Exceptions;
using Milvasoft.Core.MultiLanguage.Builder;
using Milvasoft.Identity.Builder;
using Milvasoft.Localization.Builder;
using Milvasoft.Localization.Resx;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Debugging;
using System.IO.Compression;
using System.Reflection;

namespace Milvaion.Api.AppStartup;

public static partial class StartupExtensions
{
    /// <summary>
    /// Adds authorization services.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static IServiceCollection AddAuthorization(this IServiceCollection services, IConfigurationManager configuration)
    {
        var identityBuilder = services.AddMilvaIdentity<User, int>(configuration)
                                      .WithOptions()
                                      .WithDefaultTokenManager()
                                      .WithDefaultUserManager();

        services.AddSingleton(identityBuilder.IdentityOptions);

        services.AddAuthorization();

        services.AddAuthentication(option =>
        {
            option.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            option.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            option.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false;
            options.Authority = identityBuilder.IdentityOptions.Token.TokenValidationParameters.ValidIssuer;
            options.TokenValidationParameters = identityBuilder.IdentityOptions.Token.TokenValidationParameters;

            options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(60);

            options.Events = new JwtBearerEvents
            {
                // This event is fired when the token is not provided or after OnForbidden and OnAuthenticationFailed events.
                OnChallenge = context =>
                {
                    // We will add this check and response rewrite when the token is not provided.
                    // At the same time, since I set the response code in the OnForbidden and OnAuthenticationFailed events, it was added in order not to rewrite the response a second time.
                    if (!(context.Response.StatusCode == StatusCodes.Status403Forbidden || context.Response.StatusCode == StatusCodes.Status401Unauthorized))
                    {
                        // Since this scenario will work when a token is not sent to an endpoint that requires authorization, I set the response to 401.
                        context.HttpContext.Response.ThrowWithUnauthorized();
                    }

                    return Task.CompletedTask;
                },
                OnForbidden = context =>
                {
                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    // Following if statement is redirects OnAuthenticationFailed again on 419.
                    if (context.Response.StatusCode is StatusCodes.Status419AuthenticationTimeout or StatusCodes.Status401Unauthorized
                        || AccountController.LoginEndpointPaths.Exists(e => context.Request.Path.Value.EndsWith(e)))
                        return Task.CompletedTask;

                    if (context.Exception is SecurityTokenExpiredException)
                    {
                        context.Response.StatusCode = StatusCodes.Status419AuthenticationTimeout;
                        throw new MilvaUserFriendlyException();
                    }
                    else
                    {
                        // Invalid token
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        throw new MilvaUserFriendlyException();
                    }
                }
            };
        });

        return services;
    }

    /// <summary>
    /// Adds api versioning.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(x =>
        {
            x.AssumeDefaultVersionWhenUnspecified = true;
            x.DefaultApiVersion = ApiVersion.Default;
            x.ReportApiVersions = true;
            x.ApiVersionReader = ApiVersionReader.Combine(new QueryStringApiVersionReader("api-version"),
                                                          new HeaderApiVersionReader("api-version"),
                                                          new UrlSegmentApiVersionReader());
        }).AddApiExplorer(x =>
        {
            x.GroupNameFormat = "'v'V";
            x.SubstituteApiVersionInUrl = true;
        });

        return services;
    }

    /// <summary>
    /// Adds api versioning.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static IServiceCollection AddCorsFromConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        var corsConfig = configuration.GetSection("Cors").Get<CorsOptionsConfig>() ?? throw new InvalidOperationException("Cors configuration missing");

        if (corsConfig.Policies.Count == 0)
            throw new InvalidOperationException("No CORS policies defined");

        services.AddCors(options =>
        {
            foreach (var (policyName, policy) in corsConfig.Policies)
            {
                options.AddPolicy(policyName, corsBuilder =>
                {
                    // ORIGINS
                    if (policy.Origins.Any(x => x.Equals("All", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (policy.AllowCredentials)
                            throw new InvalidOperationException(
                                $"CORS '{policyName}': When AllowCredentials=true cannot use Origins=All.");

                        corsBuilder.AllowAnyOrigin();
                    }
                    else
                    {
                        corsBuilder.WithOrigins(policy.Origins);
                    }

                    // METHODS
                    if (policy.Methods.Any(x => x.Equals("All", StringComparison.OrdinalIgnoreCase)))
                        corsBuilder.AllowAnyMethod();
                    else
                        corsBuilder.WithMethods(policy.Methods);

                    // HEADERS
                    if (policy.Headers.Any(x => x.Equals("All", StringComparison.OrdinalIgnoreCase)))
                        corsBuilder.AllowAnyHeader();
                    else
                        corsBuilder.WithHeaders(policy.Headers);

                    if (policy.ExposedHeaders.Length != 0)
                        corsBuilder.WithExposedHeaders(policy.ExposedHeaders);

                    // CREDENTIALS
                    if (policy.AllowCredentials)
                        corsBuilder.AllowCredentials();
                    else
                        corsBuilder.DisallowCredentials();
                });
            }
        });

        return services;
    }

    /// <summary>
    /// Adds openapi services.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="assemblies"></param>
    /// <returns></returns>
    public static IServiceCollection AddOpenApi(this IServiceCollection services, Assembly[] assemblies)
    {
        services.AddXmlComponentsForOpenApi(assemblies);

        services.AddOpenApi(GlobalConstant.DefaultApiVersion, options =>
        {
            // Specify the OpenAPI version to use
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
            options.AddDocumentTransformer<ApiInfoTransformer>();
            options.AddSchemaTransformer<ExampleSchemaTransformer>();

            options.AddMilvaTransformers();
        });

        return services;
    }

    /// <summary>
    /// Adds brotli and gzip response compression.
    /// </summary>
    /// <param name="services"></param>
    public static void AddAndConfigureResponseCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });

        services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });
    }

    /// <summary>
    /// Adds milva multi language services.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    public static void AddMultiLanguageSupport(this IServiceCollection services, IConfigurationManager configuration)
    {
        services.AddMilvaLocalization(configuration)
                .WithResxManager<SharedResource>()
                .PostConfigureResxLocalizationOptions(opt =>
                {
                    opt.ResourcesPath = Path.Combine(GlobalConstant.LocalizationResourcesFolderName, GlobalConstant.ResourcesFolderName);
                    opt.ResourcesFolderPath = Path.Combine(Environment.CurrentDirectory, GlobalConstant.LocalizationResourcesFolderName, GlobalConstant.ResourcesFolderName);
                });

        services.AddMilvaMultiLanguage()
                .WithDefaultMultiLanguageManager();
    }

    /// <summary>
    /// Adds serilog logging services.
    /// </summary>
    /// <param name="builder"></param>
    public static void AddObservibilityAndLogging(this WebApplicationBuilder builder)
    {
        // Serilog logs to console.
        SelfLog.Enable(Console.Error);

        builder.Host.UseSerilog((_, lc) => lc.ReadFrom.Configuration(builder.Configuration).ApplyLoggingFromConfig(builder));

        var enabled = builder.Configuration.GetSection("MilvaionConfig:OpenTelemetry:Enabled").Get<bool>();

        if (!enabled)
            Log.Logger.Information("OpenTelemetry observability disabled.");

        var serviceName = builder.Configuration.GetSection("MilvaionConfig:OpenTelemetry:Service")?.Get<string>() ?? "milvaion-api";
        var environment = builder.Configuration.GetSection("MilvaionConfig:OpenTelemetry:Environment")?.Get<string>() ?? Environment.GetEnvironmentVariable("MILVA_ENV");
        var job = builder.Configuration.GetSection("MilvaionConfig:OpenTelemetry:Job")?.Get<string>() ?? "api";
        var instance = builder.Configuration.GetSection("MilvaionConfig:OpenTelemetry:Instance")?.Get<string>() ?? Environment.MachineName;

        builder.Services.AddOpenTelemetry()
                        .ConfigureResource(resource => resource.AddService(serviceName).AddAttributes(
                        [
                            new KeyValuePair<string, object>("service", serviceName),
                            new KeyValuePair<string, object>("environment", environment),
                            new KeyValuePair<string, object>("job", job),
                            new KeyValuePair<string, object>("instance", instance),
                        ]))
                        .WithMetrics(metricBuilder =>
                        {
                            string[] diagnosticsMetrics =
                            [
                                "System.Net.Http",
                                "System.Net.NameResolution",
                                "System.Threading",
                                "System.Runtime",
                                "Microsoft.EntityFrameworkCore"
                            ];

                            metricBuilder.AddMeter(serviceName)
                                         .ConfigureResource(resource => resource.AddService(serviceName))
                                         .SetExemplarFilter(ExemplarFilterType.TraceBased)
                                         .AddAspNetCoreInstrumentation()
                                         .AddHttpClientInstrumentation()
                                         .AddProcessInstrumentation()
                                         .AddNpgsqlInstrumentation()
                                         .AddMeter(diagnosticsMetrics)
                                         .AddPrometheusExporter(); // Expose metrics via HTTP endpoint
                        })
                        .WithTracing(tracingBuilder =>
                        {
                            tracingBuilder.AddSource(GlobalConstant.ActivitySource.Name)
                                          .ConfigureResource(resource => resource.AddService(serviceName).AddAttributes(
                                          [
                                              new KeyValuePair<string, object>("service", serviceName),
                                              new KeyValuePair<string, object>("environment", environment),
                                              new KeyValuePair<string, object>("job", job),
                                              new KeyValuePair<string, object>("instance", instance),
                                          ]))
                                          .AddAspNetCoreInstrumentation(options =>
                                          {
                                              options.RecordException = true;
                                          })
                                          .AddHttpClientInstrumentation()
                                          .AddNpgsql()
                                          .AddEntityFrameworkCoreInstrumentation();
                        });
    }

    private static LoggerConfiguration ApplyLoggingFromConfig(this LoggerConfiguration loggerConfig, WebApplicationBuilder builder)
    {
        loggerConfig.WriteTo.Console();

        var seqEnabled = builder.Configuration.GetSection("MilvaionConfig:Logging:Seq:Enabled").Get<bool>();

        if (seqEnabled)
        {
            var seqUri = builder.Configuration.GetSection("MilvaionConfig:Logging:Seq:Uri").Get<string>();

            if (!string.IsNullOrWhiteSpace(seqUri))
                loggerConfig.WriteTo.Seq(seqUri);
        }

        loggerConfig.Enrich.WithProperty("AppName", "milvaion-api")
                    .Enrich.WithProperty("Environment", MilvaionExtensions.GetCurrentEnvironment())
                    .Enrich.With(new RemoveTypeTagEnricher());

        loggerConfig.Filter.ByExcluding(logEvent => logEvent.Properties.ContainsKey("RequestPath") &&
                                                    GlobalConstant.IgnoringLogPaths.Any(p => logEvent.Properties["RequestPath"].ToString().Contains(p)));

        return loggerConfig;
    }
}