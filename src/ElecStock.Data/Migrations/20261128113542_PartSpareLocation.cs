using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElecStock.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartSpareLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SpareLocationId",
                table: "Parts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Parts_SpareLocationId",
                table: "Parts",
                column: "SpareLocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Parts_Locations_SpareLocationId",
                table: "Parts",
                column: "SpareLocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Parts_Locations_SpareLocationId",
                table: "Parts");

            migrationBuilder.DropIndex(
                name: "IX_Parts_SpareLocationId",
                table: "Parts");

            migrationBuilder.DropColumn(
                name: "SpareLocationId",
                table: "Parts");
        }
    }
}
