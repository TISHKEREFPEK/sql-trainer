using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Classroom.Storage.Migrations
{
    /// <inheritdoc/>
    public partial class TaskRevisions : Migration
    {
        /// <inheritdoc/>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(name: "TaskRevisions", columns: table => new { TaskId = table.Column<string>(type: "TEXT", nullable: false), Revision = table.Column<int>(type: "INTEGER", nullable: false), Json = table.Column<string>(type: "TEXT", nullable: false), AlternativesJson = table.Column<string>(type: "TEXT", nullable: false), Restricted = table.Column<bool>(type: "INTEGER", nullable: false), Deleted = table.Column<bool>(type: "INTEGER", nullable: false), At = table.Column<long>(type: "INTEGER", nullable: false) }, constraints: table =>
            {
                table.PrimaryKey("PK_TaskRevisions", x => new { x.TaskId, x.Revision });
                table.ForeignKey(name: "FK_TaskRevisions_Tasks_TaskId", column: x => x.TaskId, principalTable: "Tasks", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            });
        }

        /// <inheritdoc/>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TaskRevisions");
        }
    }
}
