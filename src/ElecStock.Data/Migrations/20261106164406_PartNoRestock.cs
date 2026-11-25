using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElecStock.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartNoRestock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NoRestock",
                table: "Parts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NoRestock",
                table: "Parts");
        }
    }
}
