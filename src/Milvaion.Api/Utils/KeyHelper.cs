using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Milvaion.Api.Utils;

/// <summary>
/// Result of a successful api key signature validation.
/// </summary>
/// <param name="ApiKeyId">Id of the <see cref="MilvaionApiKey"/> record the key was issued for.</param>
/// <param name="KeyVersion">Version of the signing secret the key was issued with.</param>
/// <param name="ExpiresAt">UTC expiry embedded in the key, if any.</param>
public record ApiKeyValidationResult(int ApiKeyId, int KeyVersion, DateTime? ExpiresAt);

/// <summary>
/// Api key helpers for api authorization.
/// </summary>
/// <remarks>
/// A Milvaion api key is a JWT signed with <c>MilvaionConfig.ApiKey</c>. The signature proves the key was issued
/// by this installation; the <c>jti</c> claim identifies which <see cref="MilvaionApiKey"/> record it belongs to.
/// Authorization always requires the record lookup as well, otherwise revocation would be impossible.
/// </remarks>
public static class KeyHelper
{
    /// <summary>
    /// Claim carrying the version of the signing secret used to issue the key.
    /// </summary>
    public const string KeyVersionClaimName = "kv";

    /// <summary>
    /// Generates a signed api key for the given api key record.
    /// </summary>
    /// <param name="secret">Signing secret. Comes from <c>MilvaionConfig.ApiKey</c>.</param>
    /// <param name="apiKeyId">Id of the persisted <see cref="MilvaionApiKey"/> record.</param>
    /// <param name="keyVersion">Version of the signing secret.</param>
    /// <param name="expiresAt">
    /// UTC expiry of the key. Pass null for a key that never expires - revocation is then the only way to
    /// invalidate it, which is exactly why the record lookup exists.
    /// </param>
    public static string GenerateApiKey(byte[] secret, int apiKeyId, int keyVersion, DateTime? expiresAt)
    {
        var tokenHandler = new JwtSecurityTokenHandler();

        var claimsIdentity = new ClaimsIdentity();

        claimsIdentity.AddClaim(new Claim(JwtRegisteredClaimNames.Jti, apiKeyId.ToString()));
        claimsIdentity.AddClaim(new Claim(KeyVersionClaimName, keyVersion.ToString()));

        if (expiresAt.HasValue)
            claimsIdentity.AddClaim(new Claim(ClaimTypes.Expired, ((DateTimeOffset)expiresAt.Value).ToUnixTimeSeconds().ToString()));

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = claimsIdentity,
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(secret), SecurityAlgorithms.HmacSha256Signature),
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);

        return tokenHandler.WriteToken(token);
    }

    /// <summary>
    /// Validates the signature and expiry of the given key and returns the claims needed to look up its record.
    /// Returns null when the key is malformed, tampered with or expired.
    /// </summary>
    /// <remarks>
    /// A non-null result means the key was genuinely issued by this installation. It does <b>not</b> mean the key
    /// is still active - the caller must load the <see cref="MilvaionApiKey"/> record and check
    /// <c>RevokedAt</c> before honouring it.
    /// </remarks>
    public static ApiKeyValidationResult ValidateApiKey(string token, byte[] secret)
    {
        var principal = GetPrincipalForAccessKey(token, secret);

        if (!(principal?.Identity?.IsAuthenticated ?? false))
            return null;

        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);

        if (!int.TryParse(jti, out var apiKeyId))
            return null;

        _ = int.TryParse(principal.FindFirstValue(KeyVersionClaimName), out var keyVersion);

        DateTime? expiresAt = null;

        var expiredClaim = principal.FindFirstValue(ClaimTypes.Expired);

        if (!string.IsNullOrWhiteSpace(expiredClaim) && long.TryParse(expiredClaim, out var unixTime))
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime;

            if (DateTime.UtcNow >= expiresAt)
                return null;
        }

        return new ApiKeyValidationResult(apiKeyId, keyVersion, expiresAt);
    }

    /// <summary>
    /// Masks a generated key for display. Only the trailing characters are kept, which is enough for a user to
    /// match a listed key against the one in their configuration and useless to anybody else.
    /// </summary>
    public static string Mask(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length <= 6)
            return "……";

        return $"……{key[^6..]}";
    }

    /// <summary>
    /// Returns claims in the token, or null when the token is invalid.
    /// </summary>
    private static ClaimsPrincipal GetPrincipalForAccessKey(string token, byte[] secret)
    {
        try
        {
            JwtSecurityTokenHandler tokenHandler = new();

            if (!tokenHandler.CanReadToken(token))
                return null;

            TokenValidationParameters parameters = new()
            {
                // Keys without an expiry are legitimate, so expiration is checked explicitly above instead.
                RequireExpirationTime = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidateIssuer = false,
                ValidateAudience = false,
                IssuerSigningKey = new SymmetricSecurityKey(secret),
                ClockSkew = TimeSpan.FromMinutes(1),
            };

            ClaimsPrincipal principal = tokenHandler.ValidateToken(token, parameters, out SecurityToken _);

            return principal;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
