using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Milvaion.Domain.JsonModels;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Milvaion.Api.Migrations;

/// <inheritdoc />
/// <remarks>
/// Introduces projects (docs/githubdocs/PROJECTS-DESIGN.md, phase 1). Everything that exists joins the Default project.
/// <list type="bullet">
/// <item>Every ProjectId column is added with the constant default 1. PostgreSQL stores such a default in the catalog,
/// so even the largest tables are not rewritten. The NOT NULL columns keep the default for now, because the reporter
/// worker and other raw SQL writers insert rows without naming the column; it is dropped once they set it.</item>
/// <item>Foreign keys are added NOT VALID and validated after the transaction has committed. Validation only takes a
/// lock that lets writes continue, and the brief exclusive locks of the column changes are not held during the scan.</item>
/// <item>The occurrence, log and run tables get no index on ProjectId yet: every row has the same value until other
/// projects exist. Their foreign keys are created here in SQL and are not part of the EF model, for the same reason.</item>
/// </list>
/// </remarks>
public partial class _20261113233320_AddProjects : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ExclusionCalendars_Name",
            table: "ExclusionCalendars");

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "Workflows",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "WorkflowRuns",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "ScheduledJobs",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<byte>(
            name: "Scope",
            table: "Roles",
            type: "smallint",
            nullable: false,
            defaultValue: (byte)0);

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "MilvaionApiKeys",
            type: "integer",
            nullable: true,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "MetricReports",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "JobOccurrences",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "JobOccurrenceLogs",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "FailedOccurrences",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "ExclusionCalendars",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "ProjectId",
            table: "ActivityLogs",
            type: "integer",
            nullable: true,
            defaultValue: 1);

        migrationBuilder.CreateTable(
            name: "Projects",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Settings = table.Column<ProjectSettingsDocument>(type: "jsonb", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Projects", x => x.Id);
            });

        migrationBuilder.Sql("""
            INSERT INTO "Projects" ("Id", "Slug", "Name", "Description", "IsDefault", "Settings", "CreationDate", "CreatorUserName")
            OVERRIDING SYSTEM VALUE
            VALUES (1, 'default', 'Default', 'Everything that existed before projects were introduced.', TRUE, '{}', now() at time zone 'utc', 'System');

            SELECT setval(pg_get_serial_sequence('"Projects"', 'Id'), 1, true);
            """);

        migrationBuilder.Sql("""
            ALTER TABLE "MilvaionApiKeys" ALTER COLUMN "ProjectId" DROP DEFAULT;
            ALTER TABLE "ActivityLogs" ALTER COLUMN "ProjectId" DROP DEFAULT;
            """);

        migrationBuilder.CreateTable(
            name: "ProjectMemberships",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ProjectId = table.Column<int>(type: "integer", nullable: false),
                UserId = table.Column<int>(type: "integer", nullable: false),
                RoleId = table.Column<int>(type: "integer", nullable: false),
                Source = table.Column<byte>(type: "smallint", nullable: false),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectMemberships", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProjectMemberships_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ProjectMemberships_Roles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "Roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ProjectMemberships_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Workflows_ProjectId_IsActive",
            table: "Workflows",
            columns: new[] { "ProjectId", "IsActive" });

        migrationBuilder.CreateIndex(
            name: "IX_ScheduledJobs_ProjectId_IsActive",
            table: "ScheduledJobs",
            columns: new[] { "ProjectId", "IsActive" });

        migrationBuilder.CreateIndex(
            name: "IX_MilvaionApiKeys_ProjectId",
            table: "MilvaionApiKeys",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_MetricReports_ProjectId_MetricType_GeneratedAt",
            table: "MetricReports",
            columns: new[] { "ProjectId", "MetricType", "GeneratedAt" },
            descending: new[] { false, false, true });

        migrationBuilder.CreateIndex(
            name: "IX_FailedOccurrences_ProjectId_Resolved_FailedAt",
            table: "FailedOccurrences",
            columns: new[] { "ProjectId", "Resolved", "FailedAt" },
            descending: new[] { false, false, true })
            .Annotation("Npgsql:CreatedConcurrently", true);

        migrationBuilder.CreateIndex(
            name: "IX_ExclusionCalendars_ProjectId_Name",
            table: "ExclusionCalendars",
            columns: new[] { "ProjectId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ActivityLogs_ProjectId_ActivityDate",
            table: "ActivityLogs",
            columns: new[] { "ProjectId", "ActivityDate" },
            descending: new[] { false, true })
            .Annotation("Npgsql:CreatedConcurrently", true);

        migrationBuilder.CreateIndex(
            name: "IX_ProjectMemberships_ProjectId_UserId",
            table: "ProjectMemberships",
            columns: new[] { "ProjectId", "UserId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProjectMemberships_RoleId",
            table: "ProjectMemberships",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "IX_ProjectMemberships_UserId",
            table: "ProjectMemberships",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Projects_Slug",
            table: "Projects",
            column: "Slug",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_ExclusionCalendars_Projects_ProjectId",
            table: "ExclusionCalendars",
            column: "ProjectId",
            principalTable: "Projects",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_MetricReports_Projects_ProjectId",
            table: "MetricReports",
            column: "ProjectId",
            principalTable: "Projects",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_MilvaionApiKeys_Projects_ProjectId",
            table: "MilvaionApiKeys",
            column: "ProjectId",
            principalTable: "Projects",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_ScheduledJobs_Projects_ProjectId",
            table: "ScheduledJobs",
            column: "ProjectId",
            principalTable: "Projects",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_Workflows_Projects_ProjectId",
            table: "Workflows",
            column: "ProjectId",
            principalTable: "Projects",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.Sql("""
            ALTER TABLE "ActivityLogs" ADD CONSTRAINT "FK_ActivityLogs_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE SET NULL NOT VALID;
            ALTER TABLE "FailedOccurrences" ADD CONSTRAINT "FK_FailedOccurrences_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE RESTRICT NOT VALID;
            ALTER TABLE "JobOccurrences" ADD CONSTRAINT "FK_JobOccurrences_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE RESTRICT NOT VALID;
            ALTER TABLE "JobOccurrenceLogs" ADD CONSTRAINT "FK_JobOccurrenceLogs_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE RESTRICT NOT VALID;
            ALTER TABLE "WorkflowRuns" ADD CONSTRAINT "FK_WorkflowRuns_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE RESTRICT NOT VALID;
            """);

        foreach (var table in new[] { "ActivityLogs", "FailedOccurrences", "JobOccurrences", "JobOccurrenceLogs", "WorkflowRuns" })
            migrationBuilder.Sql($"""ALTER TABLE "{table}" VALIDATE CONSTRAINT "FK_{table}_Projects_ProjectId";""", suppressTransaction: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "JobOccurrences" DROP CONSTRAINT IF EXISTS "FK_JobOccurrences_Projects_ProjectId";
            ALTER TABLE "JobOccurrenceLogs" DROP CONSTRAINT IF EXISTS "FK_JobOccurrenceLogs_Projects_ProjectId";
            ALTER TABLE "WorkflowRuns" DROP CONSTRAINT IF EXISTS "FK_WorkflowRuns_Projects_ProjectId";
            """);

        migrationBuilder.DropForeignKey(
            name: "FK_ActivityLogs_Projects_ProjectId",
            table: "ActivityLogs");

        migrationBuilder.DropForeignKey(
            name: "FK_ExclusionCalendars_Projects_ProjectId",
            table: "ExclusionCalendars");

        migrationBuilder.DropForeignKey(
            name: "FK_FailedOccurrences_Projects_ProjectId",
            table: "FailedOccurrences");

        migrationBuilder.DropForeignKey(
            name: "FK_MetricReports_Projects_ProjectId",
            table: "MetricReports");

        migrationBuilder.DropForeignKey(
            name: "FK_MilvaionApiKeys_Projects_ProjectId",
            table: "MilvaionApiKeys");

        migrationBuilder.DropForeignKey(
            name: "FK_ScheduledJobs_Projects_ProjectId",
            table: "ScheduledJobs");

        migrationBuilder.DropForeignKey(
            name: "FK_Workflows_Projects_ProjectId",
            table: "Workflows");

        migrationBuilder.DropTable(
            name: "ProjectMemberships");

        migrationBuilder.DropTable(
            name: "Projects");

        migrationBuilder.DropIndex(
            name: "IX_Workflows_ProjectId_IsActive",
            table: "Workflows");

        migrationBuilder.DropIndex(
            name: "IX_ScheduledJobs_ProjectId_IsActive",
            table: "ScheduledJobs");

        migrationBuilder.DropIndex(
            name: "IX_MilvaionApiKeys_ProjectId",
            table: "MilvaionApiKeys");

        migrationBuilder.DropIndex(
            name: "IX_MetricReports_ProjectId_MetricType_GeneratedAt",
            table: "MetricReports");

        migrationBuilder.DropIndex(
            name: "IX_FailedOccurrences_ProjectId_Resolved_FailedAt",
            table: "FailedOccurrences")
            .Annotation("Npgsql:CreatedConcurrently", true);

        migrationBuilder.DropIndex(
            name: "IX_ExclusionCalendars_ProjectId_Name",
            table: "ExclusionCalendars");

        migrationBuilder.DropIndex(
            name: "IX_ActivityLogs_ProjectId_ActivityDate",
            table: "ActivityLogs")
            .Annotation("Npgsql:CreatedConcurrently", true);

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "Workflows");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "WorkflowRuns");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "ScheduledJobs");

        migrationBuilder.DropColumn(
            name: "Scope",
            table: "Roles");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "MilvaionApiKeys");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "MetricReports");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "JobOccurrences");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "JobOccurrenceLogs");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "FailedOccurrences");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "ExclusionCalendars");

        migrationBuilder.DropColumn(
            name: "ProjectId",
            table: "ActivityLogs");

        migrationBuilder.CreateIndex(
            name: "IX_ExclusionCalendars_Name",
            table: "ExclusionCalendars",
            column: "Name",
            unique: true);
    }
}
