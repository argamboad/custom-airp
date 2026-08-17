using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airp.Infrastructure.Storage.Local.Migrations
{
    /// <inheritdoc />
    public partial class WorldStateFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Facts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    ValidFromSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ValidToSequence = table.Column<long>(type: "INTEGER", nullable: true),
                    SupersededById = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Facts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Facts_ConversationId_ValidToSequence",
                table: "Facts",
                columns: new[] { "ConversationId", "ValidToSequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Facts");
        }
    }
}
