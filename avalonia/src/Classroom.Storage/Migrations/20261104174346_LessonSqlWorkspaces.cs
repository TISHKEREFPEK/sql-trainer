using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class LessonSqlWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireNewSolution",
                table: "LessonSqlTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LessonId",
                table: "Attempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkScope",
                table: "Attempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LessonSqlSnapshots",
                columns: table => new
                {
                    LessonId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    Project = table.Column<string>(type: "TEXT", nullable: false),
                    Base64 = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonSqlSnapshots", x => new { x.LessonId, x.StudentId, x.Project });
                    table.ForeignKey(
                        name: "FK_LessonSqlSnapshots_LessonSessions_LessonId",
                        column: x => x.LessonId,
                        principalTable: "LessonSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LessonSqlSnapshots_Profiles_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LessonSqlWorks",
                columns: table => new
                {
                    LessonId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    PolicyTaskId = table.Column<string>(type: "TEXT", nullable: false),
                    Project = table.Column<string>(type: "TEXT", nullable: true),
                    RequireNewSolution = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PreviousJson = table.Column<string>(type: "TEXT", nullable: true),
                    PreviousAcceptedCode = table.Column<string>(type: "TEXT", nullable: true),
                    PreviousCompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
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
                    table.PrimaryKey("PK_LessonSqlWorks", x => new { x.LessonId, x.StudentId, x.TaskId });
                    table.ForeignKey(
                        name: "FK_LessonSqlWorks_LessonSqlTasks_LessonId_PolicyTaskId",
                        columns: x => new { x.LessonId, x.PolicyTaskId },
                        principalTable: "LessonSqlTasks",
                        principalColumns: new[] { "LessonId", "TaskId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LessonSqlWorks_Profiles_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_LessonId",
                table: "Attempts",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonSqlSnapshots_StudentId",
                table: "LessonSqlSnapshots",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonSqlWorks_LessonId_PolicyTaskId",
                table: "LessonSqlWorks",
                columns: new[] { "LessonId", "PolicyTaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_LessonSqlWorks_StudentId_Project_CreatedAt",
                table: "LessonSqlWorks",
                columns: new[] { "StudentId", "Project", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Attempts_LessonSessions_LessonId",
                table: "Attempts",
                column: "LessonId",
                principalTable: "LessonSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Attempts_LessonSessions_LessonId",
                table: "Attempts");

            migrationBuilder.DropTable(
                name: "LessonSqlSnapshots");

            migrationBuilder.DropTable(
                name: "LessonSqlWorks");

            migrationBuilder.DropIndex(
                name: "IX_Attempts_LessonId",
                table: "Attempts");

            migrationBuilder.DropColumn(
                name: "RequireNewSolution",
                table: "LessonSqlTasks");

            migrationBuilder.DropColumn(
                name: "LessonId",
                table: "Attempts");

            migrationBuilder.DropColumn(
                name: "WorkScope",
                table: "Attempts");
        }
    }
}
