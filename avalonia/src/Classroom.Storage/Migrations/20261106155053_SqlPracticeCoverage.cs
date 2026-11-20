using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SqlPracticeCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SqlPracticeTrackingAt",
                table: "LessonSessions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SqlPracticeTrackingAt",
                table: "LessonSessions");
        }
    }
}
