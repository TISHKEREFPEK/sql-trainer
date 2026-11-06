using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ElectronicsAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignmentId",
                table: "ElectronicsWorks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignmentId",
                table: "ElectronicsReports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ElectronicsTasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    StarterJson = table.Column<string>(type: "TEXT", nullable: true),
                    Restricted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicsTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ElectronicsTaskRevisions",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    StarterJson = table.Column<string>(type: "TEXT", nullable: true),
                    Restricted = table.Column<bool>(type: "INTEGER", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicsTaskRevisions", x => new { x.TaskId, x.Revision });
                    table.ForeignKey(
                        name: "FK_ElectronicsTaskRevisions_ElectronicsTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ElectronicsTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LessonElectronicsTasks",
                columns: table => new
                {
                    LessonId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    StarterJson = table.Column<string>(type: "TEXT", nullable: true),
                    Restricted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Withdrawn = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonElectronicsTasks", x => new { x.LessonId, x.TaskId });
                    table.ForeignKey(
                        name: "FK_LessonElectronicsTasks_ElectronicsTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ElectronicsTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LessonElectronicsTasks_LessonSessions_LessonId",
                        column: x => x.LessonId,
                        principalTable: "LessonSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ElectronicsAssignments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    LessonId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    InitialProjectJson = table.Column<string>(type: "TEXT", nullable: false),
                    OpenedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CanSubmit = table.Column<bool>(type: "INTEGER", nullable: false),
                    StateVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicsAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElectronicsAssignments_LessonElectronicsTasks_LessonId_TaskId",
                        columns: x => new { x.LessonId, x.TaskId },
                        principalTable: "LessonElectronicsTasks",
                        principalColumns: new[] { "LessonId", "TaskId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ElectronicsAssignments_Profiles_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsWorks_AssignmentId",
                table: "ElectronicsWorks",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsReports_AssignmentId",
                table: "ElectronicsReports",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsAssignments_LessonId_StudentId_TaskId",
                table: "ElectronicsAssignments",
                columns: new[] { "LessonId", "StudentId", "TaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsAssignments_LessonId_TaskId",
                table: "ElectronicsAssignments",
                columns: new[] { "LessonId", "TaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsAssignments_StudentId",
                table: "ElectronicsAssignments",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonElectronicsTasks_TaskId",
                table: "LessonElectronicsTasks",
                column: "TaskId");

            migrationBuilder.AddForeignKey(
                name: "FK_ElectronicsReports_ElectronicsAssignments_AssignmentId",
                table: "ElectronicsReports",
                column: "AssignmentId",
                principalTable: "ElectronicsAssignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ElectronicsWorks_ElectronicsAssignments_AssignmentId",
                table: "ElectronicsWorks",
                column: "AssignmentId",
                principalTable: "ElectronicsAssignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ElectronicsReports_ElectronicsAssignments_AssignmentId",
                table: "ElectronicsReports");

            migrationBuilder.DropForeignKey(
                name: "FK_ElectronicsWorks_ElectronicsAssignments_AssignmentId",
                table: "ElectronicsWorks");

            migrationBuilder.DropTable(
                name: "ElectronicsAssignments");

            migrationBuilder.DropTable(
                name: "ElectronicsTaskRevisions");

            migrationBuilder.DropTable(
                name: "LessonElectronicsTasks");

            migrationBuilder.DropTable(
                name: "ElectronicsTasks");

            migrationBuilder.DropIndex(
                name: "IX_ElectronicsWorks_AssignmentId",
                table: "ElectronicsWorks");

            migrationBuilder.DropIndex(
                name: "IX_ElectronicsReports_AssignmentId",
                table: "ElectronicsReports");

            migrationBuilder.DropColumn(
                name: "AssignmentId",
                table: "ElectronicsWorks");

            migrationBuilder.DropColumn(
                name: "AssignmentId",
                table: "ElectronicsReports");
        }
    }
}
