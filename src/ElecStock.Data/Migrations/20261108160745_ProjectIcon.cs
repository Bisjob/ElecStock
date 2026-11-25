using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElecStock.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectIcon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IconPath",
                table: "Projects",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IconPath",
                table: "Projects");
        }
    }
}
