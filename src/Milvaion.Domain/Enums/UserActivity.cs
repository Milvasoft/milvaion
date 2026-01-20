namespace Milvaion.Domain.Enums;
/// <summary>
/// User activity types.
/// </summary>
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
public enum UserActivity : byte
{
    CreateUser,
    UpdateUser,
    DeleteUser,
    CreateRole,
    UpdateRole,
    DeleteRole,
    CreateNamespace,
    UpdateNamespace,
    DeleteNamespace,
    CreateResourceGroup,
    UpdateResourceGroup,
    DeleteResourceGroup,
    CreateContent,
    UpdateContent,
    DeleteContent,
    UpdateLanguages,
    CreateScheduledJob,
    UpdateScheduledJob,
    DeleteScheduledJob,
    UpdateFailedOccurrence,
    DeleteFailedOccurrence,
    DeleteJobOccurrence,
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
