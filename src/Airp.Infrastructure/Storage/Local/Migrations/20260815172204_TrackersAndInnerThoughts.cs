using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airp.Infrastructure.Storage.Local.Migrations
{
    /// <inheritdoc />
    public partial class TrackersAndInnerThoughts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InnerThoughts",
                table: "Conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Trackers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Max = table.Column<double>(type: "REAL", nullable: false),
                    Delta = table.Column<double>(type: "REAL", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Rule = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    UpdatedAtSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trackers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Trackers_ConversationId_Name",
                table: "Trackers",
                columns: new[] { "ConversationId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Trackers");

            migrationBuilder.DropColumn(
                name: "InnerThoughts",
                table: "Conversations");
        }
    }
}
