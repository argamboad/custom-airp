using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airp.Infrastructure.Storage.Local.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurableDials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DialValues",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DialValues", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DialValues_ConversationId_Key",
                table: "DialValues",
                columns: new[] { "ConversationId", "Key" },
                unique: true);

            // The original dials lived as columns on Conversations. Their values move into
            // rows here so an existing story keeps every setting it had; the columns stay in
            // the schema, dead, because dropping one rebuilds the table that holds everything.
            // InnerThoughts copies only when on: off is the pack's default and needs no row.
            migrationBuilder.Sql(
                """
                INSERT INTO DialValues (Id, ConversationId, Key, Value, UpdatedAtUtc)
                SELECT lower(hex(randomblob(16))), Id, 'lust', CAST(Lust AS TEXT), CreatedAtUtc
                FROM Conversations WHERE Lust IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO DialValues (Id, ConversationId, Key, Value, UpdatedAtUtc)
                SELECT lower(hex(randomblob(16))), Id, 'response-length', CAST(ResponseLength AS TEXT), CreatedAtUtc
                FROM Conversations WHERE ResponseLength IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO DialValues (Id, ConversationId, Key, Value, UpdatedAtUtc)
                SELECT lower(hex(randomblob(16))), Id, 'creativity', CAST(Creativity AS TEXT), CreatedAtUtc
                FROM Conversations WHERE Creativity IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO DialValues (Id, ConversationId, Key, Value, UpdatedAtUtc)
                SELECT lower(hex(randomblob(16))), Id, 'inner-thoughts', 'true', CreatedAtUtc
                FROM Conversations WHERE InnerThoughts = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DialValues");
        }
    }
}
