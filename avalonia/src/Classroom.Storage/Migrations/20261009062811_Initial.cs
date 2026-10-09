using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Classroom.Storage.Migrations
{
    /// <inheritdoc/>
    public partial class Initial : Migration
    {
        /// <inheritdoc/>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(name: "Groups", columns: table => new { Id = table.Column<string>(type: "TEXT", nullable: false), Name = table.Column<string>(type: "TEXT", nullable: false) }, constraints: table =>
            {
                table.PrimaryKey("PK_Groups", x => x.Id);
            });
            migrationBuilder.CreateTable(name: "Imports", columns: table => new { Hash = table.Column<string>(type: "TEXT", nullable: false), At = table.Column<long>(type: "INTEGER", nullable: false) }, constraints: table =>
            {
                table.PrimaryKey("PK_Imports", x => x.Hash);
            });
            migrationBuilder.CreateTable(name: "Tasks", columns: table => new { Id = table.Column<string>(type: "TEXT", nullable: false), Json = table.Column<string>(type: "TEXT", nullable: false), Revision = table.Column<int>(type: "INTEGER", nullable: false), Deleted = table.Column<bool>(type: "INTEGER", nullable: false), Restricted = table.Column<bool>(type: "INTEGER", nullable: false) }, constraints: table =>
            {
                table.PrimaryKey("PK_Tasks", x => x.Id);
            });
            migrationBuilder.CreateTable(name: "Profiles", columns: table => new { Id = table.Column<string>(type: "TEXT", nullable: false), Login = table.Column<string>(type: "TEXT", nullable: false), PasswordHash = table.Column<string>(type: "TEXT", nullable: false), Role = table.Column<string>(type: "TEXT", nullable: false), GroupId = table.Column<string>(type: "TEXT", nullable: true), Slot = table.Column<int>(type: "INTEGER", nullable: false), LockedUntil = table.Column<long>(type: "INTEGER", nullable: false), ThemeJson = table.Column<string>(type: "TEXT", nullable: false), PresetsJson = table.Column<string>(type: "TEXT", nullable: false) }, constraints: table =>
            {
                table.PrimaryKey("PK_Profiles", x => x.Id);
                table.ForeignKey(name: "FK_Profiles_Groups_GroupId", column: x => x.GroupId, principalTable: "Groups", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            });
            migrationBuilder.CreateTable(name: "Variants", columns: table => new { TaskId = table.Column<string>(type: "TEXT", nullable: false), Index = table.Column<int>(type: "INTEGER", nullable: false), Json = table.Column<string>(type: "TEXT", nullable: false) }, constraints: table =>
            {
                table.PrimaryKey("PK_Variants", x => new { x.TaskId, x.Index });
                table.ForeignKey(name: "FK_Variants_Tasks_TaskId", column: x => x.TaskId, principalTable: "Tasks", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            });
            migrationBuilder.CreateTable(name: "Assignments", columns: table => new { StudentId = table.Column<string>(type: "TEXT", nullable: false), TaskId = table.Column<string>(type: "TEXT", nullable: false), Json = table.Column<string>(type: "TEXT", nullable: false), Revision = table.Column<int>(type: "INTEGER", nullable: false), CompletedAt = table.Column<long>(type: "INTEGER", nullable: true), HintsJson = table.Column<string>(type: "TEXT", nullable: false), Draft = table.Column<string>(type: "TEXT", nullable: false), ActiveSeconds = table.Column<int>(type: "INTEGER", nullable: false), Checks = table.Column<int>(type: "INTEGER", nullable: false), Errors = table.Column<int>(type: "INTEGER", nullable: false) }, constraints: table =>
            {
                table.PrimaryKey("PK_Assignments", x => new { x.StudentId, x.TaskId });
                table.ForeignKey(name: "FK_Assignments_Profiles_StudentId", column: x => x.StudentId, principalTable: "Profiles", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            });
            migrationBuilder.CreateTable(name: "Attempts", columns: table => new { Id = table.Column<string>(type: "TEXT", nullable: false), StudentId = table.Column<string>(type: "TEXT", nullable: false), TaskId = table.Column<string>(type: "TEXT", nullable: false), Kind = table.Column<string>(type: "TEXT", nullable: false), At = table.Column<long>(type: "INTEGER", nullable: false) }, constraints: table =>
            {
                table.PrimaryKey("PK_Attempts", x => x.Id);
                table.ForeignKey(name: "FK_Attempts_Profiles_StudentId", column: x => x.StudentId, principalTable: "Profiles", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            });
            migrationBuilder.CreateTable(name: "Operations", columns: table => new { StudentId = table.Column<string>(type: "TEXT", nullable: false), Id = table.Column<string>(type: "TEXT", nullable: false), RequestHash = table.Column<string>(type: "TEXT", nullable: false), ResponseJson = table.Column<string>(type: "TEXT", nullable: false), At = table.Column<long>(type: "INTEGER", nullable: false) }, constraints: table =>
            {
                table.PrimaryKey("PK_Operations", x => new { x.StudentId, x.Id });
                table.ForeignKey(name: "FK_Operations_Profiles_StudentId", column: x => x.StudentId, principalTable: "Profiles", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            });
            migrationBuilder.CreateTable(name: "Snapshots", columns: table => new { StudentId = table.Column<string>(type: "TEXT", nullable: false), Project = table.Column<string>(type: "TEXT", nullable: false), Base64 = table.Column<string>(type: "TEXT", nullable: false) }, constraints: table =>
            {
                table.PrimaryKey("PK_Snapshots", x => new { x.StudentId, x.Project });
                table.ForeignKey(name: "FK_Snapshots_Profiles_StudentId", column: x => x.StudentId, principalTable: "Profiles", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            });
            migrationBuilder.CreateIndex(name: "IX_Attempts_StudentId", table: "Attempts", column: "StudentId");
            migrationBuilder.CreateIndex(name: "IX_Profiles_GroupId", table: "Profiles", column: "GroupId");
            migrationBuilder.CreateIndex(name: "IX_Profiles_Login", table: "Profiles", column: "Login", unique: true);
        }

        /// <inheritdoc/>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Assignments");
            migrationBuilder.DropTable(name: "Attempts");
            migrationBuilder.DropTable(name: "Imports");
            migrationBuilder.DropTable(name: "Operations");
            migrationBuilder.DropTable(name: "Snapshots");
            migrationBuilder.DropTable(name: "Variants");
            migrationBuilder.DropTable(name: "Profiles");
            migrationBuilder.DropTable(name: "Tasks");
            migrationBuilder.DropTable(name: "Groups");
        }
    }
}
