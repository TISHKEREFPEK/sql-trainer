using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SqlJournalExecutionLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperationId",
                table: "SqlExecutionEvidence",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqlExecutionEvidence_StudentId_OperationId",
                table: "SqlExecutionEvidence",
                columns: new[] { "StudentId", "OperationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SqlExecutionEvidence_StudentId_OperationId",
                table: "SqlExecutionEvidence");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "SqlExecutionEvidence");
        }
    }
}
