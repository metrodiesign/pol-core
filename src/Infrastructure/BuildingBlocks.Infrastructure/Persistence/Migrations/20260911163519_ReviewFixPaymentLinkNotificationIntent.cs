using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFixPaymentLinkNotificationIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotificationEmail",
                schema: "shop",
                table: "Orders",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationPhoneNumber",
                schema: "shop",
                table: "Orders",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOnIssue",
                schema: "shop",
                table: "Orders",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationEmail",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "NotificationPhoneNumber",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "NotifyOnIssue",
                schema: "shop",
                table: "Orders");
        }
    }
}
