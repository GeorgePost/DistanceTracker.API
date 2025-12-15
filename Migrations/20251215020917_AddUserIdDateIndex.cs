using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DistanceTracker.API.Migrations
{
    /// <inheritdoc />
    public partial class AddUserIdDateIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trips_UserId",
                table: "Trips");

            migrationBuilder.RenameColumn(
                name: "Date",
                table: "Trips",
                newName: "DateUTC");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_UserId_DateUTC",
                table: "Trips",
                columns: new[] { "UserId", "DateUTC" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trips_UserId_DateUTC",
                table: "Trips");

            migrationBuilder.RenameColumn(
                name: "DateUTC",
                table: "Trips",
                newName: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_UserId",
                table: "Trips",
                column: "UserId");
        }
    }
}
