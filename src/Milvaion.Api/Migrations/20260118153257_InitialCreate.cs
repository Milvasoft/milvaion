using Microsoft.EntityFrameworkCore.Migrations;
using Milvaion.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Milvaion.Api.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "_MigrationHistory",
            columns: table => new
            {
                MigrationId = table.Column<string>(type: "text", nullable: false),
                MigrationCompleted = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK__MigrationHistory", x => x.MigrationId);
            });

        migrationBuilder.CreateTable(
            name: "ActivityLogs",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Activity = table.Column<byte>(type: "smallint", nullable: false),
                ActivityDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", maxLength: 255, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ActivityLogs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "InternalNotifications",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                RecipientUserName = table.Column<string>(type: "text", nullable: true),
                RecipientUserId = table.Column<int>(type: "integer", nullable: false),
                Type = table.Column<byte>(type: "smallint", nullable: false),
                SeenDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Text = table.Column<string>(type: "text", nullable: true),
                Data = table.Column<string>(type: "text", nullable: true),
                RelatedEntityType = table.Column<byte>(type: "smallint", nullable: false),
                RelatedEntityId = table.Column<string>(type: "text", nullable: true),
                ActionLink = table.Column<string>(type: "text", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InternalNotifications", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Languages",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "text", nullable: true),
                Code = table.Column<string>(type: "text", nullable: true),
                Supported = table.Column<bool>(type: "boolean", nullable: false),
                IsDefault = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Languages", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MenuGroups",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Order = table.Column<int>(type: "integer", nullable: false),
                Translations = table.Column<List<MenuGroupTranslation>>(type: "jsonb", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MenuGroups", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Namespaces",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Slug = table.Column<string>(type: "text", nullable: true),
                Name = table.Column<string>(type: "text", nullable: true),
                Description = table.Column<string>(type: "text", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Namespaces", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Pages",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "text", nullable: true),
                HasCreate = table.Column<bool>(type: "boolean", nullable: false),
                HasDetail = table.Column<bool>(type: "boolean", nullable: false),
                HasEdit = table.Column<bool>(type: "boolean", nullable: false),
                HasDelete = table.Column<bool>(type: "boolean", nullable: false),
                CreatePermissions = table.Column<string>(type: "jsonb", nullable: true),
                DetailPermissions = table.Column<string>(type: "jsonb", nullable: true),
                EditPermissions = table.Column<string>(type: "jsonb", nullable: true),
                DeletePermissions = table.Column<string>(type: "jsonb", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Pages", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Permissions",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                PermissionGroup = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                PermissionGroupDescription = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Permissions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Roles",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true),
                DeletionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DeleterUserName = table.Column<string>(type: "text", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Roles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ScheduledJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DisplayName = table.Column<string>(type: "text", nullable: true),
                Description = table.Column<string>(type: "text", nullable: true),
                Tags = table.Column<string>(type: "text", nullable: true),
                JobData = table.Column<string>(type: "jsonb", nullable: true),
                ExecuteAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CronExpression = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                ConcurrentExecutionPolicy = table.Column<int>(type: "integer", nullable: false),
                WorkerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                JobNameInWorker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                RoutingPattern = table.Column<string>(type: "text", nullable: true),
                ZombieTimeoutMinutes = table.Column<int>(type: "integer", nullable: true),
                ExecutionTimeoutSeconds = table.Column<int>(type: "integer", nullable: true),
                Version = table.Column<int>(type: "integer", nullable: false),
                JobVersions = table.Column<string>(type: "jsonb", nullable: true),
                AutoDisableSettings = table.Column<JobAutoDisableSettings>(type: "jsonb", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ScheduledJobs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                Surname = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                UserType = table.Column<byte>(type: "smallint", nullable: false),
                AllowedNotifications = table.Column<string>(type: "jsonb", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true),
                DeleterUserName = table.Column<string>(type: "text", nullable: true),
                DeletionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                UserName = table.Column<string>(type: "text", nullable: true),
                NormalizedUserName = table.Column<string>(type: "text", nullable: true),
                Email = table.Column<string>(type: "text", nullable: true),
                NormalizedEmail = table.Column<string>(type: "text", nullable: true),
                EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                PasswordHash = table.Column<string>(type: "text", nullable: true),
                PhoneNumber = table.Column<string>(type: "text", nullable: true),
                PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UserSessionHistories",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserName = table.Column<string>(type: "text", nullable: true),
                AccessToken = table.Column<string>(type: "text", nullable: true),
                RefreshToken = table.Column<string>(type: "text", nullable: true),
                ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DeviceId = table.Column<string>(type: "text", nullable: true),
                UserId = table.Column<int>(type: "integer", nullable: false),
                IpAddress = table.Column<string>(type: "text", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserSessionHistories", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MenuItems",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Url = table.Column<string>(type: "text", nullable: true),
                PageName = table.Column<string>(type: "text", nullable: true),
                Order = table.Column<int>(type: "integer", nullable: false),
                PermissionOrGroupNames = table.Column<string>(type: "jsonb", nullable: true),
                Translations = table.Column<List<MenuItemTranslation>>(type: "jsonb", nullable: true),
                GroupId = table.Column<int>(type: "integer", nullable: false),
                ParentId = table.Column<int>(type: "integer", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MenuItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_MenuItems_MenuGroups_GroupId",
                    column: x => x.GroupId,
                    principalTable: "MenuGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_MenuItems_MenuItems_ParentId",
                    column: x => x.ParentId,
                    principalTable: "MenuItems",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "ResourceGroups",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Slug = table.Column<string>(type: "text", nullable: true),
                Name = table.Column<string>(type: "text", nullable: true),
                Description = table.Column<string>(type: "text", nullable: true),
                NamespaceId = table.Column<int>(type: "integer", nullable: false),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ResourceGroups", x => x.Id);
                table.ForeignKey(
                    name: "FK_ResourceGroups_Namespaces_NamespaceId",
                    column: x => x.NamespaceId,
                    principalTable: "Namespaces",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PageActions",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ActionName = table.Column<string>(type: "text", nullable: true),
                Permissions = table.Column<string>(type: "jsonb", nullable: true),
                Translations = table.Column<List<PageActionTranslation>>(type: "jsonb", nullable: true),
                PageId = table.Column<int>(type: "integer", nullable: false),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PageActions", x => x.Id);
                table.ForeignKey(
                    name: "FK_PageActions_Pages_PageId",
                    column: x => x.PageId,
                    principalTable: "Pages",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RolePermissionRelations",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                RoleId = table.Column<int>(type: "integer", nullable: false),
                PermissionId = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RolePermissionRelations", x => x.Id);
                table.ForeignKey(
                    name: "FK_RolePermissionRelations_Permissions_PermissionId",
                    column: x => x.PermissionId,
                    principalTable: "Permissions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_RolePermissionRelations_Roles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "Roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "JobOccurrences",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobName = table.Column<string>(type: "text", nullable: true),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                JobVersion = table.Column<int>(type: "integer", nullable: false),
                ZombieTimeoutMinutes = table.Column<int>(type: "integer", nullable: true),
                ExecutionTimeoutSeconds = table.Column<int>(type: "integer", nullable: true),
                CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                WorkerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DurationMs = table.Column<long>(type: "bigint", nullable: true),
                Result = table.Column<string>(type: "text", nullable: true),
                Exception = table.Column<string>(type: "text", nullable: true),
                Logs = table.Column<List<OccurrenceLog>>(type: "jsonb", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DispatchRetryCount = table.Column<int>(type: "integer", nullable: false),
                NextDispatchRetryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastHeartbeat = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                StatusChangeLogs = table.Column<List<OccurrenceStatusChangeLog>>(type: "jsonb", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JobOccurrences", x => x.Id);
                table.ForeignKey(
                    name: "FK_JobOccurrences_ScheduledJobs_JobId",
                    column: x => x.JobId,
                    principalTable: "ScheduledJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserRoleRelations",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<int>(type: "integer", nullable: false),
                RoleId = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserRoleRelations", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserRoleRelations_Roles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "Roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_UserRoleRelations_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserSessions",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserName = table.Column<string>(type: "text", nullable: true),
                AccessToken = table.Column<string>(type: "text", nullable: true),
                RefreshToken = table.Column<string>(type: "text", nullable: true),
                ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DeviceId = table.Column<string>(type: "text", nullable: true),
                IpAddress = table.Column<string>(type: "text", nullable: true),
                UserId = table.Column<int>(type: "integer", nullable: false),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserSessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserSessions_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Contents",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Key = table.Column<string>(type: "text", nullable: true),
                Value = table.Column<string>(type: "text", nullable: true),
                LanguageId = table.Column<int>(type: "integer", nullable: false),
                NamespaceSlug = table.Column<string>(type: "text", nullable: true),
                ResourceGroupSlug = table.Column<string>(type: "text", nullable: true),
                KeyAlias = table.Column<string>(type: "text", nullable: true),
                NamespaceId = table.Column<int>(type: "integer", nullable: false),
                ResourceGroupId = table.Column<int>(type: "integer", nullable: false),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Contents", x => x.Id);
                table.ForeignKey(
                    name: "FK_Contents_Namespaces_NamespaceId",
                    column: x => x.NamespaceId,
                    principalTable: "Namespaces",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_Contents_ResourceGroups_ResourceGroupId",
                    column: x => x.ResourceGroupId,
                    principalTable: "ResourceGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "FailedOccurrences",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                OccurrenceId = table.Column<Guid>(type: "uuid", nullable: false),
                CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                JobDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                JobNameInWorker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                WorkerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                JobData = table.Column<string>(type: "jsonb", nullable: true),
                Exception = table.Column<string>(type: "text", nullable: true),
                FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                RetryCount = table.Column<int>(type: "integer", nullable: false),
                FailureType = table.Column<int>(type: "integer", nullable: false),
                Resolved = table.Column<bool>(type: "boolean", nullable: false),
                ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ResolvedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                ResolutionNote = table.Column<string>(type: "text", nullable: true),
                ResolutionAction = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                OriginalExecuteAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FailedOccurrences", x => x.Id);
                table.ForeignKey(
                    name: "FK_FailedOccurrences_JobOccurrences_OccurrenceId",
                    column: x => x.OccurrenceId,
                    principalTable: "JobOccurrences",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_FailedOccurrences_ScheduledJobs_JobId",
                    column: x => x.JobId,
                    principalTable: "ScheduledJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Medias",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Value = table.Column<byte[]>(type: "bytea", nullable: true),
                Type = table.Column<string>(type: "text", nullable: true),
                ContentId = table.Column<int>(type: "integer", nullable: false),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Medias", x => x.Id);
                table.ForeignKey(
                    name: "FK_Medias_Contents_ContentId",
                    column: x => x.ContentId,
                    principalTable: "Contents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ActivityLogs_ActivityDate",
            table: "ActivityLogs",
            column: "ActivityDate",
            descending: []);

        migrationBuilder.CreateIndex(
            name: "IX_Contents_LanguageId_KeyAlias",
            table: "Contents",
            columns: ["LanguageId", "KeyAlias"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Contents_NamespaceId",
            table: "Contents",
            column: "NamespaceId");

        migrationBuilder.CreateIndex(
            name: "IX_Contents_ResourceGroupId",
            table: "Contents",
            column: "ResourceGroupId");

        migrationBuilder.CreateIndex(
            name: "IX_FailedOccurrences_FailureType_Resolved",
            table: "FailedOccurrences",
            columns: ["FailureType", "Resolved"]);

        migrationBuilder.CreateIndex(
            name: "IX_FailedOccurrences_JobId",
            table: "FailedOccurrences",
            column: "JobId");

        migrationBuilder.CreateIndex(
            name: "IX_FailedOccurrences_OccurrenceId",
            table: "FailedOccurrences",
            column: "OccurrenceId");

        migrationBuilder.CreateIndex(
            name: "IX_FailedOccurrences_Resolved_FailedAt",
            table: "FailedOccurrences",
            columns: ["Resolved", "FailedAt"],
            descending: [false, true]);

        migrationBuilder.CreateIndex(
            name: "IX_InternalNotifications_RecipientUserName",
            table: "InternalNotifications",
            column: "RecipientUserName");

        migrationBuilder.CreateIndex(
            name: "IX_JobOccurrences_CorrelationId",
            table: "JobOccurrences",
            column: "CorrelationId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_JobOccurrences_JobId_EndTime_CreatedAt",
            table: "JobOccurrences",
            columns: ["JobId", "EndTime", "CreatedAt"],
            descending: [false, true, true]);

        migrationBuilder.CreateIndex(
            name: "IX_JobOccurrences_Status_CreatedAt",
            table: "JobOccurrences",
            columns: ["Status", "CreatedAt"],
            descending: [false, true]);

        migrationBuilder.CreateIndex(
            name: "IX_JobOccurrences_Status_NextDispatchRetryAt_DispatchRetryCount",
            table: "JobOccurrences",
            columns: ["Status", "NextDispatchRetryAt", "DispatchRetryCount"]);

        migrationBuilder.CreateIndex(
            name: "IX_JobOccurrences_WorkerId_Status",
            table: "JobOccurrences",
            columns: ["WorkerId", "Status"]);

        migrationBuilder.CreateIndex(
            name: "IX_Medias_ContentId",
            table: "Medias",
            column: "ContentId");

        migrationBuilder.CreateIndex(
            name: "IX_MenuItems_GroupId",
            table: "MenuItems",
            column: "GroupId");

        migrationBuilder.CreateIndex(
            name: "IX_MenuItems_ParentId",
            table: "MenuItems",
            column: "ParentId");

        migrationBuilder.CreateIndex(
            name: "IX_Namespaces_Slug",
            table: "Namespaces",
            column: "Slug",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PageActions_PageId",
            table: "PageActions",
            column: "PageId");

        migrationBuilder.CreateIndex(
            name: "IX_Pages_Name",
            table: "Pages",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Permissions_PermissionGroup_Name",
            table: "Permissions",
            columns: ["PermissionGroup", "Name"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ResourceGroups_NamespaceId_Slug",
            table: "ResourceGroups",
            columns: ["NamespaceId", "Slug"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ResourceGroups_Slug",
            table: "ResourceGroups",
            column: "Slug");

        migrationBuilder.CreateIndex(
            name: "IX_RolePermissionRelations_PermissionId",
            table: "RolePermissionRelations",
            column: "PermissionId");

        migrationBuilder.CreateIndex(
            name: "IX_RolePermissionRelations_RoleId",
            table: "RolePermissionRelations",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "IX_ScheduledJobs_IsActive_ExecuteAt",
            table: "ScheduledJobs",
            columns: ["IsActive", "ExecuteAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_ScheduledJobs_WorkerId_IsActive",
            table: "ScheduledJobs",
            columns: ["WorkerId", "IsActive"]);

        migrationBuilder.CreateIndex(
            name: "IX_UserRoleRelations_RoleId",
            table: "UserRoleRelations",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "IX_UserRoleRelations_UserId",
            table: "UserRoleRelations",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Users_UserName_IsDeleted_DeletionDate",
            table: "Users",
            columns: ["UserName", "IsDeleted", "DeletionDate"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserSessions_UserId",
            table: "UserSessions",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_UserSessions_UserName_DeviceId",
            table: "UserSessions",
            columns: ["UserName", "DeviceId"]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "_MigrationHistory");

        migrationBuilder.DropTable(
            name: "ActivityLogs");

        migrationBuilder.DropTable(
            name: "FailedOccurrences");

        migrationBuilder.DropTable(
            name: "InternalNotifications");

        migrationBuilder.DropTable(
            name: "Languages");

        migrationBuilder.DropTable(
            name: "Medias");

        migrationBuilder.DropTable(
            name: "MenuItems");

        migrationBuilder.DropTable(
            name: "PageActions");

        migrationBuilder.DropTable(
            name: "RolePermissionRelations");

        migrationBuilder.DropTable(
            name: "UserRoleRelations");

        migrationBuilder.DropTable(
            name: "UserSessionHistories");

        migrationBuilder.DropTable(
            name: "UserSessions");

        migrationBuilder.DropTable(
            name: "JobOccurrences");

        migrationBuilder.DropTable(
            name: "Contents");

        migrationBuilder.DropTable(
            name: "MenuGroups");

        migrationBuilder.DropTable(
            name: "Pages");

        migrationBuilder.DropTable(
            name: "Permissions");

        migrationBuilder.DropTable(
            name: "Roles");

        migrationBuilder.DropTable(
            name: "Users");

        migrationBuilder.DropTable(
            name: "ScheduledJobs");

        migrationBuilder.DropTable(
            name: "ResourceGroups");

        migrationBuilder.DropTable(
            name: "Namespaces");
    }
}
