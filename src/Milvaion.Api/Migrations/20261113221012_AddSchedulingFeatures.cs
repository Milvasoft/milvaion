using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Milvaion.Domain.JsonModels;

#nullable disable

namespace Milvaion.Api.Migrations;

/// <inheritdoc />
public partial class AddSchedulingFeatures : Migration
{
    /// <inheritdoc />
    /// <remarks>
    /// Existing jobs get the values that reproduce their behaviour from before these settings existed: UTC, the Skip
    /// missed-run policy, priority 0 and no calendars. MaxCatchUpRuns gets its default of 10 so that switching a job
    /// to RunAll later does not start from a zero limit.
    /// </remarks>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<List<Guid>>(
            name: "ExclusionCalendarIds",
            table: "ScheduledJobs",
            type: "uuid[]",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxCatchUpRuns",
            table: "ScheduledJobs",
            type: "integer",
            nullable: false,
            defaultValue: 10);

        migrationBuilder.AddColumn<int>(
            name: "MissedRunPolicy",
            table: "ScheduledJobs",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "Priority",
            table: "ScheduledJobs",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "TimeZoneId",
            table: "ScheduledJobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "UTC");

        migrationBuilder.CreateTable(
            name: "ExclusionCalendars",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                Dates = table.Column<List<DateOnly>>(type: "date[]", nullable: true),
                DateRanges = table.Column<List<ExclusionDateRange>>(type: "jsonb", nullable: true),
                Weekdays = table.Column<List<int>>(type: "integer[]", nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatorUserName = table.Column<string>(type: "text", nullable: true),
                LastModificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierUserName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExclusionCalendars", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ExclusionCalendars_Name",
            table: "ExclusionCalendars",
            column: "Name",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ExclusionCalendars");

        migrationBuilder.DropColumn(
            name: "ExclusionCalendarIds",
            table: "ScheduledJobs");

        migrationBuilder.DropColumn(
            name: "MaxCatchUpRuns",
            table: "ScheduledJobs");

        migrationBuilder.DropColumn(
            name: "MissedRunPolicy",
            table: "ScheduledJobs");

        migrationBuilder.DropColumn(
            name: "Priority",
            table: "ScheduledJobs");

        migrationBuilder.DropColumn(
            name: "TimeZoneId",
            table: "ScheduledJobs");
    }
}
