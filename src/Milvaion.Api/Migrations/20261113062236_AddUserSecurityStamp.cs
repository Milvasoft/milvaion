using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Milvaion.Api.Migrations;

/// <inheritdoc />
public partial class AddUserSecurityStamp : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SecurityStamp",
            table: "Users",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SecurityStamp",
            table: "Users");
    }
}
