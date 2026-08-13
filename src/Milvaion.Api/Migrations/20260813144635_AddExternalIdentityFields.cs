using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Milvaion.Api.Migrations;

/// <inheritdoc />
public partial class AddExternalIdentityFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExternalSubject",
            table: "Users",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Issuer",
            table: "Users",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "LastLoginDate",
            table: "Users",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<byte>(
            name: "Provider",
            table: "Users",
            type: "smallint",
            nullable: false,
            defaultValue: (byte)0);

        migrationBuilder.AddColumn<byte>(
            name: "Provider",
            table: "Roles",
            type: "smallint",
            nullable: false,
            defaultValue: (byte)0);

        migrationBuilder.CreateIndex(
            name: "IX_Users_Issuer_ExternalSubject_IsDeleted_DeletionDate",
            table: "Users",
            columns: ["Issuer", "ExternalSubject", "IsDeleted", "DeletionDate"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Roles_Name_IsDeleted_DeletionDate",
            table: "Roles",
            columns: ["Name", "IsDeleted", "DeletionDate"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Users_Issuer_ExternalSubject_IsDeleted_DeletionDate",
            table: "Users");

        migrationBuilder.DropIndex(
            name: "IX_Roles_Name_IsDeleted_DeletionDate",
            table: "Roles");

        migrationBuilder.DropColumn(
            name: "ExternalSubject",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "Issuer",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "LastLoginDate",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "Provider",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "Provider",
            table: "Roles");
    }
}
