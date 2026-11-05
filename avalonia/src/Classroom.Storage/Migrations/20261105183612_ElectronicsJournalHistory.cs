using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ElectronicsJournalHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ElectronicsJournalStreams",
                columns: table => new
                {
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", nullable: false),
                    LessonId = table.Column<string>(type: "TEXT", nullable: false),
                    AssignmentId = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseJson = table.Column<string>(type: "TEXT", nullable: false),
                    TipJson = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ForkedFrom = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicsJournalStreams", x => new { x.StudentId, x.Id });
                    table.ForeignKey(
                        name: "FK_ElectronicsJournalStreams_LessonSessions_LessonId",
                        column: x => x.LessonId,
                        principalTable: "LessonSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ElectronicsJournalStreams_Profiles_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ElectronicsJournalChanges",
                columns: table => new
                {
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    StreamId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    ChangeJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    Checkpoint = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicsJournalChanges", x => new { x.StudentId, x.StreamId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_ElectronicsJournalChanges_ElectronicsJournalStreams_StudentId_StreamId",
                        columns: x => new { x.StudentId, x.StreamId },
                        principalTable: "ElectronicsJournalStreams",
                        principalColumns: new[] { "StudentId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsJournalChanges_StudentId_StreamId_EventId",
                table: "ElectronicsJournalChanges",
                columns: new[] { "StudentId", "StreamId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsJournalStreams_LessonId",
                table: "ElectronicsJournalStreams",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsJournalStreams_StudentId_LessonId_ReceivedAt",
                table: "ElectronicsJournalStreams",
                columns: new[] { "StudentId", "LessonId", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElectronicsJournalChanges");

            migrationBuilder.DropTable(
                name: "ElectronicsJournalStreams");
        }
    }
}
