using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Milvaion.Api.Migrations;

/// <inheritdoc />
public partial class _20261114133847_AddScheduledRunsAndRetryAfter : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "MaxRetryAfter",
            table: "Workflows",
            type: "integer",
            nullable: false,
            defaultValue: 10);

        migrationBuilder.AddColumn<DateTime>(
            name: "ScheduledAt",
            table: "WorkflowRuns",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "RetryAfter",
            table: "JobOccurrences",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "StepRetryAfterCount",
            table: "JobOccurrences",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "MaxRetryAfter",
            table: "Workflows");

        migrationBuilder.DropColumn(
            name: "ScheduledAt",
            table: "WorkflowRuns");

        migrationBuilder.DropColumn(
            name: "RetryAfter",
            table: "JobOccurrences");

        migrationBuilder.DropColumn(
            name: "StepRetryAfterCount",
            table: "JobOccurrences");
    }
}
