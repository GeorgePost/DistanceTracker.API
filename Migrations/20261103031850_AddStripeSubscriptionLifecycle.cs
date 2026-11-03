using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DistanceTracker.API.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeSubscriptionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StripeCancelAtPeriodEnd",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "StripeCheckoutGeneration",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StripeCheckoutSessionId",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StripeCurrentPeriodEndUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StripeLastSyncedAtUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeSubscriptionStatus",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProcessedStripeEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedStripeEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_StripeCustomerId",
                table: "AspNetUsers",
                column: "StripeCustomerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedStripeEvents");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_StripeCustomerId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeCancelAtPeriodEnd",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeCheckoutGeneration",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeCheckoutSessionId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeCurrentPeriodEndUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeLastSyncedAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeSubscriptionStatus",
                table: "AspNetUsers");
        }
    }
}
