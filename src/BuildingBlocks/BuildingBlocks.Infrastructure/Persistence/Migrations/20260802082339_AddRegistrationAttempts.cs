using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegistrationAttempts",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNo = table.Column<int>(type: "int", nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PersonType = table.Column<int>(type: "int", nullable: true),
                    IdNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProducerCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LicenseNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    PhotoObjectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PhotoContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegistrationAttempts_Users_MerchantUserId",
                        column: x => x.MerchantUserId,
                        principalSchema: "merch",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationAudits_TargetSubject",
                schema: "merch",
                table: "RegistrationAudits",
                column: "TargetSubject");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationAttempts_MerchantUserId_AttemptNo",
                schema: "merch",
                table: "RegistrationAttempts",
                columns: new[] { "MerchantUserId", "AttemptNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistrationAttempts",
                schema: "merch");

            migrationBuilder.DropIndex(
                name: "IX_RegistrationAudits_TargetSubject",
                schema: "merch",
                table: "RegistrationAudits");
        }
    }
}
