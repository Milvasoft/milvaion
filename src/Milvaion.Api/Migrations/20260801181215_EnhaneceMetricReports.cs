using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Milvaion.Api.Migrations;

/// <inheritdoc />
public partial class EnhaneceMetricReports : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "DataSizeBytes",
            table: "MetricReports",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "Period",
            table: "MetricReports",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DataSizeBytes",
            table: "MetricReports");

        migrationBuilder.DropColumn(
            name: "Period",
            table: "MetricReports");
    }
}
