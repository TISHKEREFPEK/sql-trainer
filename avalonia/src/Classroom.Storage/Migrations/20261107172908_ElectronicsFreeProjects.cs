using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ElectronicsFreeProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FreeProjectId",
                table: "ElectronicsWorks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LessonId",
                table: "ElectronicsWorks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProjectRevision",
                table: "ElectronicsWorks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FreeProjectId",
                table: "ElectronicsReports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LessonId",
                table: "ElectronicsReports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ElectronicsProjects",
                columns: table => new
                {
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CreationHash = table.Column<string>(type: "TEXT", nullable: false),
                    InitialProjectJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectHash = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Parts = table.Column<int>(type: "INTEGER", nullable: false),
                    Wires = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CanSubmit = table.Column<bool>(type: "INTEGER", nullable: false),
                    StateVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicsProjects", x => new { x.StudentId, x.Id });
                    table.ForeignKey(
                        name: "FK_ElectronicsProjects_Profiles_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ElectronicsPractice",
                columns: table => new
                {
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    LessonId = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectHash = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Parts = table.Column<int>(type: "INTEGER", nullable: false),
                    Wires = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstSavedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicsPractice", x => new { x.StudentId, x.ProjectId, x.LessonId });
                    table.ForeignKey(
                        name: "FK_ElectronicsPractice_ElectronicsProjects_StudentId_ProjectId",
                        columns: x => new { x.StudentId, x.ProjectId },
                        principalTable: "ElectronicsProjects",
                        principalColumns: new[] { "StudentId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ElectronicsPractice_LessonSessions_LessonId",
                        column: x => x.LessonId,
                        principalTable: "LessonSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsWorks_StudentId_FreeProjectId_LessonId",
                table: "ElectronicsWorks",
                columns: new[] { "StudentId", "FreeProjectId", "LessonId" });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsReports_StudentId_FreeProjectId_LessonId",
                table: "ElectronicsReports",
                columns: new[] { "StudentId", "FreeProjectId", "LessonId" });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsPractice_LessonId",
                table: "ElectronicsPractice",
                column: "LessonId");

            migrationBuilder.AddForeignKey(
                name: "FK_ElectronicsReports_ElectronicsPractice_StudentId_FreeProjectId_LessonId",
                table: "ElectronicsReports",
                columns: new[] { "StudentId", "FreeProjectId", "LessonId" },
                principalTable: "ElectronicsPractice",
                principalColumns: new[] { "StudentId", "ProjectId", "LessonId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ElectronicsWorks_ElectronicsPractice_StudentId_FreeProjectId_LessonId",
                table: "ElectronicsWorks",
                columns: new[] { "StudentId", "FreeProjectId", "LessonId" },
                principalTable: "ElectronicsPractice",
                principalColumns: new[] { "StudentId", "ProjectId", "LessonId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ElectronicsReports_ElectronicsPractice_StudentId_FreeProjectId_LessonId",
                table: "ElectronicsReports");

            migrationBuilder.DropForeignKey(
                name: "FK_ElectronicsWorks_ElectronicsPractice_StudentId_FreeProjectId_LessonId",
                table: "ElectronicsWorks");

            migrationBuilder.DropTable(
                name: "ElectronicsPractice");

            migrationBuilder.DropTable(
                name: "ElectronicsProjects");

            migrationBuilder.DropIndex(
                name: "IX_ElectronicsWorks_StudentId_FreeProjectId_LessonId",
                table: "ElectronicsWorks");

            migrationBuilder.DropIndex(
                name: "IX_ElectronicsReports_StudentId_FreeProjectId_LessonId",
                table: "ElectronicsReports");

            migrationBuilder.DropColumn(
                name: "FreeProjectId",
                table: "ElectronicsWorks");

            migrationBuilder.DropColumn(
                name: "LessonId",
                table: "ElectronicsWorks");

            migrationBuilder.DropColumn(
                name: "ProjectRevision",
                table: "ElectronicsWorks");

            migrationBuilder.DropColumn(
                name: "FreeProjectId",
                table: "ElectronicsReports");

            migrationBuilder.DropColumn(
                name: "LessonId",
                table: "ElectronicsReports");
        }
    }
}
