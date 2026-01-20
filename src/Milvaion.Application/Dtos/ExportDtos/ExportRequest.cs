using Milvaion.Application.Features.Users.GetUserList;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Dtos.ExportDtos;

/// <summary>
/// Export types.
/// </summary>
public enum ExportType : sbyte
{
    /// <summary>
    /// User list export.
    /// </summary>
    User = 1
}

/// <summary>
/// Export request.
/// </summary>
public class ExportRequest
{
    private static readonly Dictionary<ExportType, (Type Type, List<string> Permissions)> _exportTypeQueryPairs = new()
    {
        { ExportType.User, (typeof(GetUserListQuery), [PermissionCatalog.UserManagement.List] )}
    };

    /// <summary>
    /// Determines whether which data to export.
    /// </summary>
    public ExportType ExportType { get; set; }

    /// <summary>
    /// Export specifications.
    /// </summary>
    public ListRequest ListRequest { get; set; }

    /// <summary>
    /// Gets query type according to export type.
    /// </summary>
    /// <returns></returns>
    public Type GetQueryType() => _exportTypeQueryPairs[ExportType].Type;

    /// <summary>
    /// Gets required permissions according to export type.
    /// </summary>
    /// <returns></returns>
    public List<string> GetRequiredPermissions() => _exportTypeQueryPairs[ExportType].Permissions;

    /// <summary>
    /// Validates request.
    /// </summary>
    /// <returns></returns>
    public bool IsValid()
    {
        if (!Enum.IsDefined(ExportType))
            return false;

        return true;
    }
}