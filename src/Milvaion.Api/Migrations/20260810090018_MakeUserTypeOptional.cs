using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Milvaion.Api.Migrations
{
    /// <inheritdoc />
    public partial class MakeUserTypeOptional : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// The UserType property was removed from the model, but the column it maps to is still NOT NULL
        /// with no default, so every insert into Users fails with 23502. Giving the column a default lets
        /// the database fill it in for the inserts that no longer mention it.
        ///
        /// The column is deliberately kept rather than dropped: the historical values are preserved, and a
        /// rollback to a version that still reads UserType finds every row populated, including the ones
        /// written while this migration was applied. AppUser (2) is the default the removed property carried.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE "Users" ALTER COLUMN "UserType" SET DEFAULT 2;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE "Users" ALTER COLUMN "UserType" DROP DEFAULT;""");
        }
    }
}
