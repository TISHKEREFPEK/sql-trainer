using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class LessonSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FullName",
                table: "Profiles",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LessonSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TeacherId = table.Column<string>(type: "TEXT", nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", nullable: true),
                    GroupName = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PausedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AllowSql = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowElectronics = table.Column<bool>(type: "INTEGER", nullable: false),
                    OpenToAllGroups = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegistrationOpen = table.Column<bool>(type: "INTEGER", nullable: false),
                    FreePractice = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LessonSessions_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LessonSessions_Profiles_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LessonParticipants",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    JoinedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    GroupNameAtEntry = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonParticipants", x => new { x.SessionId, x.StudentId });
                    table.ForeignKey(
                        name: "FK_LessonParticipants_LessonSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "LessonSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LessonParticipants_Profiles_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LessonParticipants_StudentId",
                table: "LessonParticipants",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonSessions_GroupId",
                table: "LessonSessions",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonSessions_TeacherId",
                table: "LessonSessions",
                column: "TeacherId",
                unique: true,
                filter: "\"EndedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LessonParticipants");

            migrationBuilder.DropTable(
                name: "LessonSessions");

            migrationBuilder.DropColumn(
                name: "FullName",
                table: "Profiles");
        }
    }
}
