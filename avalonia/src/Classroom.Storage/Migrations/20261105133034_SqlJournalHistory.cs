using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SqlJournalHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SqlJournalStreams",
                columns: table => new
                {
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    LessonId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkScope = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseCode = table.Column<string>(type: "TEXT", nullable: false),
                    TipCode = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ForkedFrom = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlJournalStreams", x => new { x.StudentId, x.Id });
                    table.ForeignKey(
                        name: "FK_SqlJournalStreams_LessonSessions_LessonId",
                        column: x => x.LessonId,
                        principalTable: "LessonSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SqlJournalStreams_Profiles_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SqlJournalChanges",
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
                    table.PrimaryKey("PK_SqlJournalChanges", x => new { x.StudentId, x.StreamId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_SqlJournalChanges_SqlJournalStreams_StudentId_StreamId",
                        columns: x => new { x.StudentId, x.StreamId },
                        principalTable: "SqlJournalStreams",
                        principalColumns: new[] { "StudentId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SqlJournalChanges_StudentId_StreamId_EventId",
                table: "SqlJournalChanges",
                columns: new[] { "StudentId", "StreamId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqlJournalStreams_LessonId",
                table: "SqlJournalStreams",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_SqlJournalStreams_StudentId_LessonId_TaskId_ReceivedAt",
                table: "SqlJournalStreams",
                columns: new[] { "StudentId", "LessonId", "TaskId", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SqlJournalChanges");

            migrationBuilder.DropTable(
                name: "SqlJournalStreams");
        }
    }
}
