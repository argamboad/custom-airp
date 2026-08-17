using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Airp.Infrastructure.Storage.Local.Migrations
{
    /// <inheritdoc />
    public partial class CharacterLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CharacterName",
                table: "Conversations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CharacterName",
                table: "Conversations");
        }
    }
}
