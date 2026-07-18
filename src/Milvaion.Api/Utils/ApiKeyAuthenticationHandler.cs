using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Milvaion.Api.Services;
using Milvaion.Domain.Enums;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Milvaion.Api.Utils;

/// <summary>
/// Authenticates requests carrying an <c>X-ApiKey</c> header.
/// </summary>
/// <remarks>
/// This is an authentication scheme rather than an action filter on purpose. Producing a real
/// <see cref="ClaimsPrincipal"/> means every existing <c>[Auth(PermissionCatalog...)]</c> attribute keeps working
/// unchanged for api key callers - permissions are emitted as role claims, exactly as the login token does.
/// Had this stayed a filter, authorization would have had to be reimplemented separately for machine callers.
/// </remarks>
public class ApiKeyAuthenticationHandler(IOptionsMonitor<ApiKeyAuthenticationOptions> options,
                                         ILoggerFactory loggerFactory,
                                         UrlEncoder encoder,
                                         MilvaionConfig milvaionConfig,
                                         ApiKeyStore apiKeyStore,
                                         IMilvaionRepositoryBase<MilvaionApiKey> apiKeyRepository)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, loggerFactory, encoder)
{
    private readonly MilvaionConfig _milvaionConfig = milvaionConfig;
    private readonly ApiKeyStore _apiKeyStore = apiKeyStore;
    private readonly IMilvaionRepositoryBase<MilvaionApiKey> _apiKeyRepository = apiKeyRepository;

    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(MessageConstant.ApiKey, out var headerValues))
            return AuthenticateResult.NoResult();

        var presentedKey = headerValues.ToString();

        if (string.IsNullOrWhiteSpace(presentedKey))
            return AuthenticateResult.NoResult();

        var validationResult = KeyHelper.ValidateApiKey(presentedKey, _milvaionConfig.ApiKey.SecretBytes);

        if (validationResult == null)
            return AuthenticateResult.Fail("Invalid api key.");

        var apiKey = await _apiKeyStore.GetAsync(validationResult.ApiKeyId, Options.CacheLifetime, Context.RequestAborted);

        // The signature was valid but the record is gone - treat it as revoked rather than trusting the token.
        if (apiKey == null)
            return AuthenticateResult.Fail("Invalid api key.");

        if (apiKey.IsRevoked)
            return AuthenticateResult.Fail("Api key has been revoked.");

        if (apiKey.ExpiresAt.HasValue && DateTime.UtcNow >= apiKey.ExpiresAt.Value)
            return AuthenticateResult.Fail("Api key has expired.");

        // Rejecting stale versions explicitly gives a clear reason after a secret rotation, instead of the
        // signature check failing with no explanation.
        if (apiKey.KeyVersion != _milvaionConfig.ApiKey.Version)
            return AuthenticateResult.Fail("Api key was issued with a retired signing secret.");

        await TouchLastUsedAsync(apiKey.Id);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, apiKey.Name),
            new(ClaimTypes.NameIdentifier, apiKey.Id.ToString()),
            new(ApiKeyAuthenticationDefaults.ApiKeyIdClaimName, apiKey.Id.ToString()),
            new(GlobalConstant.UserTypeClaimName, nameof(UserType.Manager))
        };

        // Permissions travel as role claims because AuthAttribute derives from AuthorizeAttribute and matches
        // on Roles. A key with no permissions authenticates but is authorized for nothing, which is intended.
        claims.AddRange(apiKey.Permissions.Select(permission => new Claim(ClaimTypes.Role, permission)));

        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationDefaults.AuthenticationScheme);

        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Records that the key was used, at most once per configured interval.
    /// </summary>
    private async Task TouchLastUsedAsync(int apiKeyId)
    {
        try
        {
            if (!await _apiKeyStore.ShouldWriteLastUsedAsync(apiKeyId, Options.LastUsedWriteInterval))
                return;

            var entity = await _apiKeyRepository.GetByIdAsync(apiKeyId, cancellationToken: Context.RequestAborted);

            if (entity == null)
                return;

            entity.LastUsedAt = DateTime.UtcNow;

            await _apiKeyRepository.UpdateAsync(entity, Context.RequestAborted);
        }
        catch (Exception ex)
        {
            // Bookkeeping must never fail a request that is otherwise authenticated.
            Logger.LogWarning(ex, "Could not update LastUsedAt for api key {ApiKeyId}.", apiKeyId);
        }
    }
}
