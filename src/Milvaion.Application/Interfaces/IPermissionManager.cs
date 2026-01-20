using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Permission manager for managing system permissions.
/// </summary>
public interface IPermissionManager : IInterceptable
{
    /// <summary>
    /// Get all permissions as entity.
    /// </summary>
    /// <returns></returns>
    Task<List<Permission>> GetAllPermissionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Migrate permissions to the database.
    /// </summary>
    /// <returns></returns>
    Task<Response<string>> MigratePermissionsAsync(CancellationToken cancellationToken);
}
