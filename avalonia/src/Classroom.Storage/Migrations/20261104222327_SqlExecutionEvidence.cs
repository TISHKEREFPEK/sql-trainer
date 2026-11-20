using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SqlExecutionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SqlExecutionStates",
                columns: table => new
                {
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Base64 = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlExecutionStates", x => new { x.StudentId, x.Hash });
                    table.ForeignKey(
                        name: "FK_SqlExecutionStates_Profiles_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SqlExecutionEvidence",
                columns: table => new
                {
                    AttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Check = table.Column<bool>(type: "INTEGER", nullable: false),
                    BeforeHash = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlExecutionEvidence", x => x.AttemptId);
                    table.ForeignKey(
                        name: "FK_SqlExecutionEvidence_Attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "Attempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SqlExecutionEvidence_SqlExecutionStates_StudentId_BeforeHash",
                        columns: x => new { x.StudentId, x.BeforeHash },
                        principalTable: "SqlExecutionStates",
                        principalColumns: new[] { "StudentId", "Hash" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SqlExecutionEvidence_StudentId_BeforeHash",
                table: "SqlExecutionEvidence",
                columns: new[] { "StudentId", "BeforeHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SqlExecutionEvidence");

            migrationBuilder.DropTable(
                name: "SqlExecutionStates");
        }
    }
}
