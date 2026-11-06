using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ResultsRoster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResultsMode",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "lesson");

            migrationBuilder.AddColumn<bool>(
                name: "RosterAtStart",
                table: "LessonSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "RosterCapturedAt",
                table: "LessonSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LessonRoster",
                columns: table => new
                {
                    LessonId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentName = table.Column<string>(type: "TEXT", nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", nullable: true),
                    GroupName = table.Column<string>(type: "TEXT", nullable: false),
                    GroupKnown = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonRoster", x => new { x.LessonId, x.StudentId });
                    table.ForeignKey(
                        name: "FK_LessonRoster_LessonSessions_LessonId",
                        column: x => x.LessonId,
                        principalTable: "LessonSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LessonRoster_Profiles_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LessonRoster_StudentId",
                table: "LessonRoster",
                column: "StudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LessonRoster");

            migrationBuilder.DropColumn(
                name: "ResultsMode",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "RosterAtStart",
                table: "LessonSessions");

            migrationBuilder.DropColumn(
                name: "RosterCapturedAt",
                table: "LessonSessions");
        }
    }
}
