namespace Milvaion.Domain.Enums;

/// <summary>
/// Where a role applies.
/// </summary>
public enum RoleScope : byte
{
    /// <summary>
    /// Assigned to users directly. Its project permissions apply in the Default project only once projects are enabled.
    /// </summary>
    Instance = 0,

    /// <summary>
    /// Assigned through a project membership and applies in that project only.
    /// </summary>
    Project = 1
}
