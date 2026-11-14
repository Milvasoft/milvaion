namespace Milvaion.Application.Utils.Projects;

/// <summary>
/// Claim types Milvaion adds to the principals it builds.
/// </summary>
public static class MilvaionClaimTypes
{
    /// <summary>
    /// Id of the Milvaion user account. Set in local tokens and on OIDC principals after provisioning; a value the
    /// identity provider puts in its own token under this type is removed before Milvaion's is added.
    /// </summary>
    public const string UserId = "milvaion_uid";
}
