using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionManagedLocally : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ManagedLocally",
                table: "Positions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManagedLocally",
                table: "Positions");
        }
    }
}
