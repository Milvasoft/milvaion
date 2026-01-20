using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Models;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain;
using Milvaion.Domain.JsonModels;
using Milvaion.Domain.UI;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Helpers;
using Milvasoft.Core.MultiLanguage.EntityBases.Abstract;
using Milvasoft.Core.MultiLanguage.Manager;
using Milvasoft.Identity.Abstract;
using Milvasoft.Identity.Concrete;
using Milvasoft.Identity.Concrete.Options;
using Milvasoft.Interception.Ef.Transaction;
using System.Net.Sockets;
using System.Text.Json;

namespace Milvaion.Infrastructure.Persistence;

/// <summary>
/// Data seed methods.
/// </summary>
/// <param name="serviceProvider"></param>
public class DatabaseMigrator(IServiceProvider serviceProvider)
{
    private readonly MilvaionDbContext _dbContext = serviceProvider.GetService<MilvaionDbContext>();

    /// <summary>
    /// Remove, recreates and seed database for development purposes.
    /// </summary>
    /// <returns></returns>
    public async Task<Response> ResetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var configuration = serviceProvider.GetService<IConfiguration>();

        await _dbContext.Database.EnsureDeletedAsync(cancellationToken);

        var connectionString = configuration.GetConnectionString("DefaultConnectionString");

        var opt = new DbContextOptionsBuilder<MilvaionDbContext>()
                    .UseNpgsql(connectionString, b => b.MigrationsHistoryTable(TableNames.EfMigrationHistory).MigrationsAssembly("Milvaion.Api").EnableRetryOnFailure())
                    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution);

        opt.ConfigureWarnings(warnings => { warnings.Log(RelationalEventId.PendingModelChangesWarning); });

        try
        {
            using var db = new MilvaionDbContext(opt.Options);

            await db.Database.MigrateAsync(cancellationToken: cancellationToken);

        }
        catch (Exception ex)
        {
            // Retry
            if (ex.InnerException is SocketException || ex.InnerException is IOException)
            {
                using var db = new MilvaionDbContext(opt.Options);

                await db.Database.MigrateAsync(cancellationToken: cancellationToken);
            }
            else
                throw;
        }

        return Response.Success();
    }

    /// <summary>
    /// Creates default triggers.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task CreateTriggersAsync(CancellationToken cancellationToken)
    {
        var createTriggerSql = await File.ReadAllTextAsync(Path.Combine(GlobalConstant.SqlFilesPath, "create_triggers.sql"), cancellationToken);

        await _dbContext.Database.ExecuteSqlRawAsync(createTriggerSql, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Seeds default production data.
    /// </summary>
    /// <returns></returns>
    public async Task<string> SeedDefaultDataAsync(string rootPass = null, CancellationToken cancellationToken = default)
    {
        var superAdminPermission = new Permission
        {
            Id = 1,
            Name = nameof(PermissionCatalog.App.SuperAdmin),
            Description = "Provides access to the entire system.",
            NormalizedName = nameof(PermissionCatalog.App.SuperAdmin).MilvaNormalize(),
            PermissionGroup = nameof(PermissionCatalog.App),
            PermissionGroupDescription = "Application-wide permissions."
        };

        await _dbContext.Permissions.AddAsync(superAdminPermission, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var superAdminRole = new Role
        {
            Id = 1,
            Name = nameof(PermissionCatalog.App.SuperAdmin),
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername,
            RolePermissionRelations =
            [
                new()
                {
                    PermissionId = superAdminPermission.Id,
                    RoleId = 1
                }
            ]
        };

        await _dbContext.Roles.AddAsync(superAdminRole, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var rootUser = new User
        {
            Id = 1,
            UserName = GlobalConstant.RootUsername,
            NormalizedUserName = "ROOTUSER",
            Email = string.Empty,
            NormalizedEmail = string.Empty,
            Name = "Administrator",
            Surname = "User",
            UserType = Domain.Enums.UserType.Manager,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            TwoFactorEnabled = false,
            LockoutEnabled = false,
            AccessFailedCount = 0,
            RoleRelations =
            [
                new()
                {
                    RoleId = superAdminRole.Id
                }
            ]
        };

        if (rootPass is null)
        {
            rootPass = IdentityHelpers.GenerateRandomPassword(new MilvaRandomPaswordGenerationOption
            {
                Length = 16,
            });

            Console.WriteLine($"Initial root user password: {rootPass}");
        }

        _dbContext.ServiceProvider.GetService<IMilvaUserManager<User, int>>().SetPasswordHash(rootUser, rootPass);

        await _dbContext.Users.AddAsync(rootUser, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return rootPass;
    }

    /// <summary>
    /// Seeds default ui related data.
    /// </summary>
    /// <returns></returns>
    public async Task<Response> SeedUIRelatedDataAsync(CancellationToken cancellationToken = default)
    {
        await _dbContext.MenuItems.ExecuteDeleteAsync(cancellationToken);
        await _dbContext.MenuGroups.ExecuteDeleteAsync(cancellationToken);
        await _dbContext.Pages.ExecuteDeleteAsync(cancellationToken);
        await _dbContext.PageActions.ExecuteDeleteAsync(cancellationToken);

        var filePath = Path.Combine(GlobalConstant.JsonFilesPath, "ui_data.json");

        if (!File.Exists(filePath))
            return Response.Error("Cannot find file.");

        var json = await File.ReadAllTextAsync(filePath, cancellationToken);

        var data = JsonSerializer.Deserialize<UISeedModel>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (data == null)
            return Response.Error("Invalid json format!");

        // MenuGroups
        foreach (var group in data.MenuGroups)
        {
            var entity = new MenuGroup
            {
                Id = group.Id,
                Order = group.Order,
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername,
                Translations = group.Translations?.Select(t => new MenuGroupTranslation
                {
                    LanguageId = t.LanguageId,
                    Name = t.Name,
                    EntityId = group.Id
                }).ToList()
            };
            _dbContext.MenuGroups.Add(entity);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Pages
        foreach (var page in data.Pages)
        {
            _dbContext.Pages.Add(new Page
            {
                Id = page.Id,
                Name = page.Name,
                HasCreate = page.HasCreate,
                HasEdit = page.HasEdit,
                HasDetail = page.HasDetail,
                HasDelete = page.HasDelete,
                CreatePermissions = page.CreatePermissions ?? [],
                EditPermissions = page.EditPermissions ?? [],
                DetailPermissions = page.DetailPermissions ?? [],
                DeletePermissions = page.DeletePermissions ?? [],
                AdditionalActions = [.. page.AdditionalActions.Select(pa=>new PageAction
                {
                    Id = pa.Id,
                    ActionName = pa.ActionName,
                    Permissions = pa.Permissions ?? [],
                    PageId = page.Id,
                    CreatorUserName = GlobalConstant.SystemUsername,
                    Translations = pa.Translations?.Select(t => new PageActionTranslation
                    {
                        LanguageId = t.LanguageId,
                        Title = t.Name,
                        EntityId = pa.Id
                    }).ToList(),
                })],
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername
            });
        }

        // MenuItems (recursive ekleme)
        var flatItems = data.MenuItems;
        var allChildIds = new HashSet<int>();

        // Run the helper function on the top-level list
        CollectAllChildIds(flatItems);

        // Now, loop through the top-level items and skip any that are known children
        foreach (var rootItem in flatItems)
        {
            // If this ID is in the child set, it's already been processed (or will be)
            // by its parent. Skip it.
            if (allChildIds.Contains(rootItem.Id))
                continue;

            // This is a true root item, so add it and its children recursively.
            var entity = MapMenuItemRecursive(rootItem, null);

            _dbContext.MenuItems.Add(entity);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        MenuItem MapMenuItemRecursive(MenuItemModel model, int? parentId)
        {
            var item = new MenuItem
            {
                Id = model.Id,
                Order = model.Order,
                GroupId = model.GroupId,
                Url = model.Url,
                PageName = model.PageName,
                ParentId = parentId,
                PermissionOrGroupNames = model.PermissionOrGroupNames ?? [],
                Translations = [.. model.Translations.Select(t => new MenuItemTranslation
                {
                    LanguageId = t.LanguageId,
                    Name = t.Name,
                    EntityId = model.Id
                })],
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername,
                Childrens = model.Children?.Select(child => MapMenuItemRecursive(child, model.Id)).ToList() ?? []
            };

            return item;
        }

        // Helper function to find all IDs that exist in a 'children' array
        void CollectAllChildIds(IEnumerable<MenuItemModel> items)
        {
            if (items == null)
                return;

            foreach (var item in items)
            {
                if (item.Children != null && !item.Children.IsNullOrEmpty())
                {
                    foreach (var child in item.Children)
                    {
                        allChildIds.Add(child.Id);
                        CollectAllChildIds(child.Children); // Recurse just in case
                    }
                }
            }
        }

        return Response.Success();
    }

    /// <summary>
    /// Seeds fake data.
    /// </summary>
    /// <returns></returns>
    [Transaction]
    public async Task SeedFakeDataAsync(bool sameData = true, string locale = "tr", CancellationToken cancellationToken = default)
    {
        var roleFaker = new RoleFaker(sameData, locale);

        var roles = roleFaker.Generate(100);

        await _dbContext.BulkInsertAsync(roles, cancellationToken: cancellationToken);

        var userFaker = new UserFaker(sameData, locale, roles);

        var users = userFaker.Generate(250);

        await _dbContext.BulkInsertAsync(users, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Migrate default permissions.
    /// </summary>
    /// <param name="permissionManager"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task<Response<string>> MigratePermissionsAsync(IPermissionManager permissionManager, CancellationToken cancellationToken = default) => permissionManager.MigratePermissionsAsync(cancellationToken);

    /// <summary>
    /// Initial data seed and migration operation for production.
    /// </summary>
    /// <returns></returns>
    [ExcludeFromMetadata]
    public async Task<Response<string>> InitDatabaseAsync(IPermissionManager permissionManager, string rootPassword = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var initialMigrationSql = await File.ReadAllTextAsync(Path.Combine(GlobalConstant.SqlFilesPath, "initial_migration_fetch.sql"), cancellationToken);

            var initialMigration = await _dbContext.Database.SqlQueryRaw<EfMigrationHistory>(initialMigrationSql).FirstOrDefaultAsync(cancellationToken);

            if (initialMigration == null)
                return Response<string>.Error("Initial migration cannot found!");

            var migrationLog = await _dbContext.MigrationHistory.FirstOrDefaultAsync(m => m.MigrationId == initialMigration.MigrationId, cancellationToken: cancellationToken);

            if (migrationLog?.MigrationCompleted ?? false)
                return Response<string>.Error("Already initialized!");

            var rootPass = await SeedDefaultDataAsync(rootPassword, cancellationToken: cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);

            await CreateTriggersAsync(cancellationToken);

            await SeedUIRelatedDataAsync(cancellationToken);

            var languages = LanguagesSeed.Seed.Select(l => new Language
            {
                Code = l.Code,
                Name = l.Name,
                IsDefault = l.IsDefault,
                Supported = l.Supported,
            }).ToList();

            await _dbContext.Languages.AddRangeAsync(languages, cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);

            var languageSeed = languages.Cast<ILanguage>().ToList();

            MultiLanguageManager.UpdateLanguagesList(languageSeed);

            await MigratePermissionsAsync(permissionManager, cancellationToken);

            if (migrationLog is null)
            {
                migrationLog = new MigrationHistory
                {
                    MigrationId = initialMigration.MigrationId,
                    MigrationCompleted = true
                };

                await _dbContext.MigrationHistory.AddAsync(migrationLog, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                await _dbContext.MigrationHistory.Where(m => m.MigrationId == migrationLog.MigrationId)
                                                          .ExecuteUpdateAsync(i => i.SetProperty(x => x.MigrationCompleted, true), cancellationToken: cancellationToken);
            }

            return Response<string>.Success(rootPass);
        }
        catch (Exception)
        {
            return Response<string>.Error("Already initialized!");
        }
    }
}
