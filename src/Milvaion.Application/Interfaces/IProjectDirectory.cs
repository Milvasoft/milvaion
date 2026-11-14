namespace Milvaion.Application.Interfaces;

/// <summary>
/// A project as seen by request resolution.
/// </summary>
/// <param name="Id">Project id.</param>
/// <param name="Slug">Project slug.</param>
/// <param name="IsArchived">True when the project is read-only.</param>
public sealed record ProjectInfo(int Id, string Slug, bool IsArchived);

/// <summary>
/// Cached lookups used to resolve the project of a request and the caller's permissions in it.
/// </summary>
public interface IProjectDirectory
{
    /// <summary>
    /// Finds a project by slug (case-insensitive), or returns null.
    /// </summary>
    Task<ProjectInfo> FindBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a project by id, or returns null.
    /// </summary>
    Task<ProjectInfo> FindByIdAsync(int projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the id of the user with <paramref name="userName"/>, or null. Used for tokens issued before the user id claim existed.
    /// </summary>
    Task<int?> FindUserIdAsync(string userName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the permissions the user holds in the project through a membership, or null when the user is not a member.
    /// </summary>
    Task<IReadOnlyList<string>> GetMembershipPermissionsAsync(int userId, int projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets cached memberships of a user, after a membership or role change.
    /// </summary>
    void InvalidateUser(int userId);
}
