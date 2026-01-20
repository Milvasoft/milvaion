using System.ComponentModel;

namespace Milvaion.Domain.Enums;

/// <summary>
/// Determines which screen users will see after logging in to the frontend.
/// </summary>
[Flags]
public enum UserType : byte
{
    /// <summary>
    /// Can see management screens.
    /// </summary>
    [Description("Manager user type")]
    Manager = 1,

    /// <summary>
    /// Can see user profile and view screens.
    /// </summary>
    [Description("Application user type")]
    AppUser = 2
}
