using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airp.Infrastructure.Storage.Local.Migrations
{
    /// <inheritdoc />
    public partial class SpendLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Spend",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    GenerationId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CachedTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CacheWriteTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    Cost = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Spend", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Spend_AtUtc",
                table: "Spend",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Spend_ConversationId_AtUtc",
                table: "Spend",
                columns: new[] { "ConversationId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Spend_MessageId",
                table: "Spend",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Spend");
        }
    }
}
