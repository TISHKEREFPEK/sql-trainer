using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ElectronicsReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpectedAssignmentVersion",
                table: "ElectronicsReports",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ElectronicsReviews",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    StudentId = table.Column<string>(type: "TEXT", nullable: false),
                    ReportId = table.Column<string>(type: "TEXT", nullable: false),
                    TeacherId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviousReviewId = table.Column<string>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", nullable: false),
                    Grade = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectronicsReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElectronicsReviews_ElectronicsReports_StudentId_ReportId",
                        columns: x => new { x.StudentId, x.ReportId },
                        principalTable: "ElectronicsReports",
                        principalColumns: new[] { "StudentId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ElectronicsReviews_Profiles_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsReviews_StudentId_ReportId_Sequence",
                table: "ElectronicsReviews",
                columns: new[] { "StudentId", "ReportId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicsReviews_TeacherId",
                table: "ElectronicsReviews",
                column: "TeacherId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElectronicsReviews");

            migrationBuilder.DropColumn(
                name: "ExpectedAssignmentVersion",
                table: "ElectronicsReports");
        }
    }
}
