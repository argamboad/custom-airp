using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airp.Infrastructure.Storage.Local.Migrations
{
    /// <inheritdoc />
    public partial class Asides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Asides",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    Answer = table.Column<string>(type: "TEXT", nullable: false),
                    AskedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    EstimatedPromptTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    ContextAudit = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Asides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Asides_ConversationId_AskedAtUtc",
                table: "Asides",
                columns: new[] { "ConversationId", "AskedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Asides");
        }
    }
}
