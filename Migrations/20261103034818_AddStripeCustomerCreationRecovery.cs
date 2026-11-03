using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DistanceTracker.API.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeCustomerCreationRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerCreationAttemptId",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerCreationEmail",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StripeCustomerCreationNeedsReview",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "StripeCustomerCreationStartedAtUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StripeCustomerCreationAttemptId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeCustomerCreationEmail",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeCustomerCreationNeedsReview",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeCustomerCreationStartedAtUtc",
                table: "AspNetUsers");
        }
    }
}
