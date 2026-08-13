using Asp.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Milvaion.Api.Controllers;
using Milvaion.Api.Services;
using Milvaion.Api.Utils;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Domain.Enums;
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
using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;

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

        var authOptions = configuration.GetSection("MilvaionConfig:Authentication").Get<Application.Utils.Models.Options.AuthenticationOptions>() ?? new Application.Utils.Models.Options.AuthenticationOptions();
        var oidc = authOptions.Oidc ?? new OidcOptions();

        // When OIDC is on, bare [Auth] endpoints authenticate through the same issuer-routing policy scheme as
        // [Auth(permission)] does, so a Keycloak token is only ever handed to the OIDC scheme. Listing the local
        // and OIDC schemes side by side here would make the local scheme try (and reject) the Keycloak token,
        // and its OnAuthenticationFailed short-circuits the request with a 401 before the OIDC result is used.
        var authenticationSchemes = oidc.Enabled
            ? new List<string> { MultiBearerAuthenticationScheme, ApiKeyAuthenticationDefaults.AuthenticationScheme }
            : [JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationDefaults.AuthenticationScheme];

        services.AddAuthorizationBuilder()
                .SetDefaultPolicy(new AuthorizationPolicyBuilder([.. authenticationSchemes])
                                      .RequireAuthenticatedUser()
                                      .Build());

        services.AddScoped<ApiKeyStore>();
        services.AddScoped<IApiKeyCacheInvalidator>(sp => sp.GetRequiredService<ApiKeyStore>());
        services.AddScoped<IApiKeyGenerator, ApiKeyGenerator>();

        // With OIDC on, the default bearer handling is a policy scheme that routes each token to the local
        // or the external scheme by its issuer, so a plain [Auth] endpoint accepts both. The api key scheme
        // stays separate (it is selected explicitly by [ApiAuth] and keyed off the X-ApiKey header).
        var defaultBearerScheme = oidc.Enabled ? MultiBearerAuthenticationScheme : JwtBearerDefaults.AuthenticationScheme;

        var authenticationBuilder = services.AddAuthentication(option =>
        {
            option.DefaultAuthenticateScheme = defaultBearerScheme;
            option.DefaultChallengeScheme = defaultBearerScheme;
            option.DefaultScheme = defaultBearerScheme;
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
        })
        .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationDefaults.AuthenticationScheme, _ => { });

        if (oidc.Enabled)
        {
            authenticationBuilder.AddOidcScheme(OidcAuthenticationScheme, oidc);

            // Route each bearer token to the local or the external scheme by its issuer, so a plain [Auth]
            // endpoint accepts both without every attribute naming a scheme.
            authenticationBuilder.AddPolicyScheme(MultiBearerAuthenticationScheme, MultiBearerAuthenticationScheme, policyOptions =>
            {
                policyOptions.ForwardDefaultSelector = context => SelectBearerScheme(context, oidc);
            });
        }

        return services;
    }

    /// <summary>
    /// The scheme name for tokens issued by the external OpenID Connect provider, kept alongside the
    /// local bearer scheme and the api key scheme.
    /// </summary>
    public const string OidcAuthenticationScheme = "Oidc";

    /// <summary>
    /// The default bearer scheme when OIDC is enabled: a policy scheme that forwards each token to the
    /// local or the external scheme based on its issuer.
    /// </summary>
    public const string MultiBearerAuthenticationScheme = "MultiBearer";

    /// <summary>
    /// Picks the scheme for the incoming bearer token: the external scheme when the token's issuer is one
    /// the OIDC provider is configured for, otherwise the local scheme. Non-bearer requests fall through to
    /// the local scheme, which challenges as before.
    /// </summary>
    private static string SelectBearerScheme(HttpContext context, OidcOptions oidc)
    {
        var header = context.Request.Headers.Authorization.ToString();

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header["Bearer ".Length..].Trim();
            var issuer = TryReadIssuer(token);

            if (!string.IsNullOrEmpty(issuer) && IssuerBelongsToOidc(issuer, oidc))
                return OidcAuthenticationScheme;
        }

        return JwtBearerDefaults.AuthenticationScheme;
    }

    private static string TryReadIssuer(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();

            return handler.CanReadToken(token) ? handler.ReadJwtToken(token).Issuer : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IssuerBelongsToOidc(string issuer, OidcOptions oidc)
    {
        if (string.Equals(issuer, oidc.Authority, StringComparison.OrdinalIgnoreCase))
            return true;

        var validIssuers = (oidc.ValidIssuers ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return validIssuers.Any(candidate => string.Equals(candidate, issuer, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Registers the external OIDC bearer scheme. Tokens are validated against the provider's metadata
    /// and, on validation, the user is provisioned locally and the request principal is enriched with
    /// the permission claims the authorization stack matches on. Identity comes from the provider;
    /// permissions stay owned in Milvaion.
    /// </summary>
    private static AuthenticationBuilder AddOidcScheme(this AuthenticationBuilder builder, string scheme, OidcOptions oidc)
        => builder.AddJwtBearer(scheme, options =>
        {
            options.Authority = oidc.Authority;

            // Keep the raw JWT claim names (sub, preferred_username, roles, email, given_name, ...) instead of
            // remapping them to the legacy WS-* URIs, so the provisioning below can read them by their standard
            // names. Without this, FindFirst("sub") returns null because "sub" is remapped to nameidentifier.
            options.MapInboundClaims = false;

            // When the API reaches the provider on a different host than the browser (containerized API vs a
            // browser on the host), discovery and the JWKS come from here instead of the browser-facing Authority.
            if (!string.IsNullOrWhiteSpace(oidc.MetadataAddress))
                options.MetadataAddress = oidc.MetadataAddress;

            options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;

            if (!string.IsNullOrWhiteSpace(oidc.Audience))
                options.Audience = oidc.Audience;

            // The token issuer can differ from the discovery host in that split setup, so accept the configured list. Empty falls back to the issuer from the discovery document.
            var validIssuers = (oidc.ValidIssuers ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = oidc.NameClaim,
                RoleClaimType = ClaimTypes.Role,
                ValidateAudience = !string.IsNullOrWhiteSpace(oidc.Audience),
                ValidateIssuer = true,
                ValidIssuers = validIssuers.Length > 0 ? validIssuers : null
            };

            options.Events = new JwtBearerEvents
            {
                // Logged under our own category so it is not caught by the JwtBearerHandler log filter that
                // suppresses "ProcessingMessageFailed". Surfaces the real reason (issuer, metadata, signature).
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OidcAuthentication");
                    logger.LogWarning(context.Exception, "OIDC bearer authentication failed.");
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    if (context.Principal?.Identity is not ClaimsIdentity identity)
                        return;

                    var principal = context.Principal;

                    string First(string type) => principal.FindFirst(type)?.Value;

                    var subject = First(oidc.SubjectClaim) ?? First("sub");

                    if (string.IsNullOrWhiteSpace(subject))
                        return;

                    var roleNames = principal.FindAll(oidc.RolesClaim)
                                             .Select(c => c.Value)
                                             .Where(r => string.IsNullOrEmpty(oidc.RolePrefix) || r.StartsWith(oidc.RolePrefix, StringComparison.OrdinalIgnoreCase))
                                             .ToList();

                    var descriptor = new ExternalIdentityDescriptor
                    {
                        Provider = ExternalProvider.Oidc,
                        Issuer = First("iss") ?? oidc.Authority,
                        Subject = subject,
                        UserName = First(oidc.NameClaim) ?? First("preferred_username") ?? subject,
                        Email = First("email"),
                        Name = First("given_name"),
                        Surname = First("family_name"),
                        RoleNames = roleNames
                    };

                    // Cache-first resolve, provisioning under a per-identity distributed lock on a miss, lives in
                    // the service: most requests do no database work and the sign-in request burst resolves to a
                    // single provision instead of racing inserts.
                    var externalIdentityService = context.HttpContext.RequestServices.GetRequiredService<IExternalIdentityService>();

                    var claims = await externalIdentityService.GetOrBuildClaimsAsync(descriptor, context.HttpContext.RequestAborted);

                    identity.AddClaims(claims);
                }
            };
        });

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
                                         .AddMeter(Infrastructure.Telemetry.BackgroundServiceMetrics.MeterName) // Custom background service metrics
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

        loggerConfig.Filter.ByExcluding(logEvent =>
            logEvent.Properties.TryGetValue("SourceContext", out var sourceContext)
            //&& sourceContext.ToString().Contains("JwtBearerHandler")
            && logEvent.Properties.TryGetValue("EventId", out var eventId)
            /*&& eventId.ToString().Contains("ProcessingMessageFailed")*/);

        return loggerConfig;
    }
}