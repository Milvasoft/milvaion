using Milvasoft.Core.Abstractions;
using Milvasoft.Identity.Concrete;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Account manager for user account operations.
/// </summary>
public interface IAccountManager : IInterceptable
{
    /// <summary>
    /// User login operation.
    /// It updates sessions in database.
    /// Generates access and refresh tokens.
    /// </summary>
    /// <returns></returns>
    Task<MilvaToken> LoginAsync(User user, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates token for ci user.
    /// Generates access token.
    /// </summary>
    /// <returns></returns>
    string GenerateToken(User user, DateTime? expiryDate = null);
}
