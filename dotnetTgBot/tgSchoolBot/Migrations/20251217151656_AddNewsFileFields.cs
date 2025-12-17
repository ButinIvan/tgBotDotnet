using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace tgSchoolBot.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsFileFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "News",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilePath",
                table: "News",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileName",
                table: "News");

            migrationBuilder.DropColumn(
                name: "FilePath",
                table: "News");
        }
    }
}
