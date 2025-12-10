using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace tgSchoolBot.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleClassesPerAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Classes_AdminTelegramUserId",
                table: "Classes");

            migrationBuilder.CreateIndex(
                name: "IX_Classes_AdminTelegramUserId",
                table: "Classes",
                column: "AdminTelegramUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Classes_AdminTelegramUserId",
                table: "Classes");

            migrationBuilder.CreateIndex(
                name: "IX_Classes_AdminTelegramUserId",
                table: "Classes",
                column: "AdminTelegramUserId",
                unique: true);
        }
    }
}
