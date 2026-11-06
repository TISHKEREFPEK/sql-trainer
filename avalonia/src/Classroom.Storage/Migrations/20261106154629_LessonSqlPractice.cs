using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class LessonSqlPractice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LessonSqlPractice",
                columns: table => new
                {
                    LessonId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Partial = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    HintsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Draft = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Checks = table.Column<int>(type: "INTEGER", nullable: false),
                    Errors = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastActivityAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastHeartbeatAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AcceptedCode = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveSecondsAtCompletion = table.Column<int>(type: "INTEGER", nullable: true),
                    TrackingVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonSqlPractice", x => new { x.LessonId, x.StudentId, x.TaskId });
                    table.ForeignKey(
                        name: "FK_LessonSqlPractice_LessonSessions_LessonId",
                        column: x => x.LessonId,
                        principalTable: "LessonSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LessonSqlPractice_Profiles_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LessonSqlPractice_StudentId",
                table: "LessonSqlPractice",
                column: "StudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LessonSqlPractice");
        }
    }
}
