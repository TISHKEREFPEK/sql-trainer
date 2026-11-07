using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ElectronicsActiveIntervals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ActivityEndedAt",
                table: "ElectronicsJournalChanges",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ActivityStartedAt",
                table: "ElectronicsJournalChanges",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityEndedAt",
                table: "ElectronicsJournalChanges");

            migrationBuilder.DropColumn(
                name: "ActivityStartedAt",
                table: "ElectronicsJournalChanges");
        }
    }
}
