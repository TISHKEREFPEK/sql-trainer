using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Classroom.Storage.Migrations
{
    /// <inheritdoc />
    public partial class LearningHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Attempts_StudentId",
                table: "Attempts");

            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "Attempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Attempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Explanation",
                table: "Attempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcceptedCode",
                table: "Assignments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActiveSecondsAtCompletion",
                table: "Assignments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastActivityAt",
                table: "Assignments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastHeartbeatAt",
                table: "Assignments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StartedAt",
                table: "Assignments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrackingVersion",
                table: "Assignments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_StudentId_TaskId_At",
                table: "Attempts",
                columns: new[] { "StudentId", "TaskId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Attempts_StudentId_TaskId_At",
                table: "Attempts");

            migrationBuilder.DropColumn(
                name: "Action",
                table: "Attempts");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Attempts");

            migrationBuilder.DropColumn(
                name: "Explanation",
                table: "Attempts");

            migrationBuilder.DropColumn(
                name: "AcceptedCode",
                table: "Assignments");

            migrationBuilder.DropColumn(
                name: "ActiveSecondsAtCompletion",
                table: "Assignments");

            migrationBuilder.DropColumn(
                name: "LastActivityAt",
                table: "Assignments");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "Assignments");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "Assignments");

            migrationBuilder.DropColumn(
                name: "TrackingVersion",
                table: "Assignments");

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_StudentId",
                table: "Attempts",
                column: "StudentId");
        }
    }
}
