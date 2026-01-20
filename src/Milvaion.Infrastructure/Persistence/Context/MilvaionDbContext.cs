using Microsoft.EntityFrameworkCore;
using Milvaion.Domain;
using Milvaion.Domain.ContentManagement;
using Milvaion.Domain.UI;
using Milvasoft.DataAccess.EfCore.Bulk.DbContextBase;
using Milvasoft.DataAccess.EfCore.Configuration;

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

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

    /// <inheritdoc/>
    public override int SaveChanges()
    {
        SetGuidV7();
        return base.SaveChanges();
    }

    /// <inheritdoc/>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SetGuidV7();
        return base.SaveChangesAsync(cancellationToken);
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
        modelBuilder.Entity<FailedOccurrence>().Property(x => x.Id).ValueGeneratedNever();

        base.OnModelCreating(modelBuilder);
    }
}