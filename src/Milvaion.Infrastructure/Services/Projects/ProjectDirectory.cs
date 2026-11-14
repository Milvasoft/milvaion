using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence.Context;

namespace Milvaion.Infrastructure.Services.Projects;

/// <summary>
/// Default <see cref="IProjectDirectory"/>. Results are cached in memory for <see cref="ProjectsOptions.CacheSeconds"/>,
/// so a membership change on another API instance takes effect within that time.
/// </summary>
public sealed class ProjectDirectory(IServiceScopeFactory scopeFactory, IMemoryCache cache, IOptionsMonitor<ProjectsOptions> options) : IProjectDirectory
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IMemoryCache _cache = cache;
    private readonly IOptionsMonitor<ProjectsOptions> _options = options;

    private TimeSpan CacheLifetime => TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.CacheSeconds));

    /// <inheritdoc/>
    public async Task<ProjectInfo> FindBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var normalized = slug.Trim().ToLowerInvariant();

        return await GetOrLoadAsync($"milvaion:project:slug:{normalized}",
                                    db => db.Projects.Where(p => p.Slug == normalized)
                                                     .Select(p => new ProjectInfo(p.Id, p.Slug, p.ArchivedAt != null))
                                                     .FirstOrDefaultAsync(cancellationToken));
    }

    /// <inheritdoc/>
    public Task<ProjectInfo> FindByIdAsync(int projectId, CancellationToken cancellationToken = default)
        => GetOrLoadAsync($"milvaion:project:id:{projectId}",
                          db => db.Projects.Where(p => p.Id == projectId)
                                           .Select(p => new ProjectInfo(p.Id, p.Slug, p.ArchivedAt != null))
                                           .FirstOrDefaultAsync(cancellationToken));

    /// <inheritdoc/>
    public async Task<int?> FindUserIdAsync(string userName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return null;

        var holder = await GetOrLoadAsync($"milvaion:project:user:{userName}",
                                          async db => new UserIdHolder(await db.Users.Where(u => u.UserName == userName && !u.IsDeleted)
                                                                                     .Select(u => (int?)u.Id)
                                                                                     .FirstOrDefaultAsync(cancellationToken)));

        return holder.UserId;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetMembershipPermissionsAsync(int userId, int projectId, CancellationToken cancellationToken = default)
    {
        var memberships = await GetOrLoadAsync(MembershipKey(userId), db => LoadMembershipsAsync(db, userId, cancellationToken));

        return memberships.TryGetValue(projectId, out var permissions) ? permissions : null;
    }

    /// <inheritdoc/>
    public void InvalidateUser(int userId) => _cache.Remove(MembershipKey(userId));

    private static async Task<Dictionary<int, IReadOnlyList<string>>> LoadMembershipsAsync(MilvaionDbContext db, int userId, CancellationToken cancellationToken)
    {
        var rows = await db.ProjectMemberships.Where(m => m.UserId == userId)
                                              .Select(m => new
                                              {
                                                  m.ProjectId,
                                                  // Only project roles grant project permissions through a membership.
                                                  Permissions = m.Role.Scope == RoleScope.Project && !m.Role.IsDeleted
                                                      ? m.Role.RolePermissionRelations.Select(r => r.Permission.PermissionGroup + "." + r.Permission.Name).ToList()
                                                      : new List<string>()
                                              })
                                              .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.ProjectId, r => (IReadOnlyList<string>)r.Permissions);
    }

    private static string MembershipKey(int userId) => $"milvaion:project:memberships:{userId}";

    private async Task<T> GetOrLoadAsync<T>(string key, Func<MilvaionDbContext, Task<T>> load)
    {
        if (_cache.TryGetValue(key, out T cached))
            return cached;

        await using var scope = _scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        var value = await load(db);

        // Misses are cached too (a null project), so a client sending an unknown slug cannot make every request hit the database.
        _cache.Set(key, value, CacheLifetime);

        return value;
    }

    private sealed record UserIdHolder(int? UserId);
}
