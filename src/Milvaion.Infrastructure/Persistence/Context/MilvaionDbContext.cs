using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Interfaces;
using Milvaion.Domain;
using Milvaion.Domain.ContentManagement;
using Milvaion.Domain.UI;
using Milvasoft.DataAccess.EfCore.Bulk.DbContextBase;
using Milvasoft.DataAccess.EfCore.Configuration;
using System.Linq.Expressions;

namespace Milvaion.Infrastructure.Persistence.Context;

/// <summary>
/// Handles all database operations. Inherits <see cref="MilvaionDbContext"/>
/// </summary>
///
/// <remarks>
/// <para> You must register <see cref="IDataAccessConfiguration"/> in your application startup. </para>
/// <para> If <see cref="IDataAccessConfiguration"/>'s AuditDeleter, AuditModifier or AuditCreator is true
///        and HttpMethod is POST,PUT or DELETE it will gets performer user in constructor from database.
///        This can affect performance little bit. But you want audit every record easily you must use this :( </para>
/// </remarks>
public class MilvaionDbContext(DbContextOptions options) : MilvaBulkDbContext(options)
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public DbSet<MigrationHistory> MigrationHistory { get; set; }
    public DbSet<EfMigrationHistory> EfMigrationHistory { get; set; }
    public DbSet<ActivityLog> ActivityLogs { get; set; }
    public DbSet<Permission> Permissions { get; set; }
    public DbSet<Role> Roles { get; set; }
    public DbSet<RolePermissionRelation> RolePermissionRelations { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<UserRoleRelation> UserRoleRelations { get; set; }
    public DbSet<UserSession> UserSessions { get; set; }
    public DbSet<UserSessionHistory> UserSessionHistories { get; set; }
    public DbSet<MenuItem> MenuItems { get; set; }
    public DbSet<MenuGroup> MenuGroups { get; set; }
    public DbSet<Page> Pages { get; set; }
    public DbSet<PageAction> PageActions { get; set; }
    public DbSet<Content> Contents { get; set; }
    public DbSet<Media> Medias { get; set; }
    public DbSet<ResourceGroup> ResourceGroups { get; set; }
    public DbSet<Namespace> Namespaces { get; set; }
    public DbSet<Language> Languages { get; set; }
    public DbSet<InternalNotification> InternalNotifications { get; set; }
    public DbSet<ScheduledJob> ScheduledJobs { get; set; }
    public DbSet<JobOccurrence> JobOccurrences { get; set; }
    public DbSet<FailedOccurrence> FailedOccurrences { get; set; }
    public DbSet<JobOccurrenceLog> JobOccurrenceLogs { get; set; }
    public DbSet<Workflow> Workflows { get; set; }
    public DbSet<WorkflowRun> WorkflowRuns { get; set; }
    public DbSet<MetricReport> MetricReports { get; set; }
    public DbSet<MilvaionApiKey> ApiKeys { get; set; }
    public DbSet<MilvaionApiKeyPermissionRelation> ApiKeyPermissionRelations { get; set; }
    public DbSet<AppSetting> AppSettings { get; set; }
    public DbSet<ExclusionCalendar> ExclusionCalendars { get; set; }
    public DbSet<Project> Projects { get; set; }
    public DbSet<ProjectMembership> ProjectMemberships { get; set; }

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

    /// <summary>
    /// Name of the query filter that restricts project data to the current project.
    /// Use <c>IgnoreQueryFilters([MilvaionDbContext.ProjectFilterName])</c> only in code that deliberately works across projects.
    /// </summary>
    public const string ProjectFilterName = "Project";

    /// <summary>
    /// The project context of the scope this instance was leased in. Set by <see cref="MilvaionDbContextScopedFactory"/>.
    /// A context created directly with <c>new</c> (migrations, test setup) has none and sees every project.
    /// </summary>
    public IProjectContext ProjectContext { get; set; }

    /// <summary>
    /// Read by the project query filter on every query. Must stay an instance member so EF evaluates it per query.
    /// </summary>
    public bool ProjectFilterBypass => ProjectContext?.BypassFilter ?? true;

    /// <summary>
    /// Read by the project query filter on every query. -1 matches no row.
    /// </summary>
    public int ProjectFilterId => ProjectContext?.ProjectId ?? -1;

    /// <inheritdoc/>
    /// <remarks>
    /// MilvaDbContext runs its save logic in the bool overloads only (every other SaveChanges overload ends here),
    /// so the GUIDv7 assignment lives here too.
    /// </remarks>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SetGuidV7();
        ProjectSaveRules.Apply(ChangeTracker, ProjectContext);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// MilvaDbContext runs its save logic in the bool overloads only (every other SaveChangesAsync overload ends here),
    /// so the GUIDv7 assignment lives here too.
    /// </remarks>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SetGuidV7();
        ProjectSaveRules.Apply(ChangeTracker, ProjectContext);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc/>
    private void SetGuidV7()
    {
        var entries = ChangeTracker.Entries().Where(e => e.State == EntityState.Added && e.Metadata.FindProperty("Id")?.ClrType == typeof(Guid));

        foreach (var entry in entries)
        {
            var idProp = entry.Property("Id");

            if ((Guid)idProp.CurrentValue! == Guid.Empty)
            {
                idProp.CurrentValue = Guid.CreateVersion7();
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScheduledJob>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<JobOccurrence>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<JobOccurrenceLog>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<FailedOccurrence>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<Workflow>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<WorkflowRun>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<MetricReport>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<ExclusionCalendar>().Property(x => x.Id).ValueGeneratedNever();

        base.OnModelCreating(modelBuilder);

        ConfigureProjects(modelBuilder);
    }

    /// <summary>
    /// Project foreign keys, indexes and the project query filter. See docs/githubdocs/PROJECTS-DESIGN.md.
    /// </summary>
    private void ConfigureProjects(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectMembership>().HasOne(m => m.Project).WithMany(p => p.Memberships).HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ProjectMembership>().HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ProjectMembership>().HasOne(m => m.Role).WithMany().HasForeignKey(m => m.RoleId).OnDelete(DeleteBehavior.Cascade);

        // Restrict: a project that still owns data cannot be deleted by accident.
        // JobOccurrences, JobOccurrenceLogs and WorkflowRuns have the same foreign key, but it is created by the
        // AddProjects migration in SQL and left out of the model: EF would otherwise add an index on ProjectId alone to
        // the three largest tables, which costs every insert while all rows hold the same value.
        modelBuilder.Entity<ScheduledJob>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<FailedOccurrence>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Workflow>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MetricReport>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ExclusionCalendar>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MilvaionApiKey>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ActivityLog>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.SetNull);

        // List screens of the smaller tables. The large tables (occurrences, logs, runs) get project-leading indexes
        // when projects are enabled by default: until then the filter is bypassed and those indexes would only
        // slow down writes.
        modelBuilder.Entity<ScheduledJob>().HasIndex(e => new { e.ProjectId, e.IsActive });
        modelBuilder.Entity<Workflow>().HasIndex(e => new { e.ProjectId, e.IsActive });
        modelBuilder.Entity<FailedOccurrence>().HasIndex(e => new { e.ProjectId, e.Resolved, e.FailedAt }).IsDescending(false, false, true).IsCreatedConcurrently();
        modelBuilder.Entity<MetricReport>().HasIndex(e => new { e.ProjectId, e.MetricType, e.GeneratedAt }).IsDescending(false, false, true);
        modelBuilder.Entity<ActivityLog>().HasIndex(e => new { e.ProjectId, e.ActivityDate }).IsDescending(false, true).IsCreatedConcurrently();

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => !t.IsOwned()).ToList())
        {
            if (typeof(IProjectScoped).IsAssignableFrom(entityType.ClrType))
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(ProjectFilterName, BuildProjectFilter(entityType.ClrType, nullable: false));
            else if (typeof(IOptionalProjectScoped).IsAssignableFrom(entityType.ClrType))
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(ProjectFilterName, BuildProjectFilter(entityType.ClrType, nullable: true));
        }
    }

    /// <summary>
    /// Builds <c>e => ProjectFilterBypass || e.ProjectId == ProjectFilterId</c> for <paramref name="clrType"/>.
    /// The context members are referenced through a constant of this instance, which is how EF recognises them as
    /// per-query parameters rather than values baked into the cached model.
    /// </summary>
    private LambdaExpression BuildProjectFilter(Type clrType, bool nullable)
    {
        var entity = Expression.Parameter(clrType, "e");
        var context = Expression.Constant(this);
        var bypass = Expression.Property(context, nameof(ProjectFilterBypass));
        Expression projectId = Expression.Property(context, nameof(ProjectFilterId));
        Expression entityProjectId = Expression.Property(entity, nameof(IProjectScoped.ProjectId));

        if (nullable)
            projectId = Expression.Convert(projectId, typeof(int?));

        return Expression.Lambda(Expression.OrElse(bypass, Expression.Equal(entityProjectId, projectId)), entity);
    }
}