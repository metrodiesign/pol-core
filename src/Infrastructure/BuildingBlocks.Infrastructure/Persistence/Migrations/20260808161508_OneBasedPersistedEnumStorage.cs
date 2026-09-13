using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OneBasedPersistedEnumStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preflight is the first operation: NULL IdentityType values are never guessed or defaulted.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM merch.Users WHERE [IdentityType] IS NULL)
                    THROW 50001, 'One-based enum migration refused: merch.Users.IdentityType contains NULL.', 1;
                IF EXISTS (SELECT 1 FROM merch.RegistrationAttempts WHERE [IdentityType] IS NULL)
                    THROW 50001, 'One-based enum migration refused: merch.RegistrationAttempts.IdentityType contains NULL.', 1;

                IF EXISTS (SELECT 1 FROM admin.Sessions WHERE [Status] IS NULL OR [Status] NOT IN (0, 1, 2))
                    THROW 50001, 'One-based enum migration refused: admin.Sessions.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM admin.Users WHERE [Tier] IS NULL OR [Tier] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: admin.Users.Tier contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM admin.Users WHERE [Status] IS NULL OR [Status] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: admin.Users.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM iam.PermissionGroups WHERE [Scope] IS NULL OR [Scope] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: iam.PermissionGroups.Scope contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM iam.PermissionGroups WHERE [Status] IS NULL OR [Status] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: iam.PermissionGroups.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM iam.Permissions WHERE [Status] IS NULL OR [Status] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: iam.Permissions.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM iam.Roles WHERE [Status] IS NULL OR [Status] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: iam.Roles.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM iam.Roles WHERE [Scope] IS NULL OR [Scope] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: iam.Roles.Scope contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM cfg.Positions WHERE [Status] IS NULL OR [Status] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: cfg.Positions.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM cfg.Offices WHERE [Status] IS NULL OR [Status] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: cfg.Offices.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM cfg.Levels WHERE [Status] IS NULL OR [Status] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: cfg.Levels.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM cfg.Divisions WHERE [Status] IS NULL OR [Status] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: cfg.Divisions.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM merch.Merchants WHERE [Status] IS NULL OR [Status] NOT IN (0))
                    THROW 50001, 'One-based enum migration refused: merch.Merchants.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM merch.RegistrationAttempts WHERE [Purpose] IS NULL OR [Purpose] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: merch.RegistrationAttempts.Purpose contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM merch.RegistrationAttempts WHERE [IdentityType] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: merch.RegistrationAttempts.IdentityType contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM merch.Sessions WHERE [Status] IS NULL OR [Status] NOT IN (0, 1, 2))
                    THROW 50001, 'One-based enum migration refused: merch.Sessions.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM merch.Users WHERE [Status] IS NULL OR [Status] NOT IN (0, 1, 2, 3))
                    THROW 50001, 'One-based enum migration refused: merch.Users.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM merch.Users WHERE [IdentityType] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: merch.Users.IdentityType contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM shop.Orders WHERE [Status] IS NULL OR [Status] NOT IN (0, 1, 2, 3, 4, 5))
                    THROW 50001, 'One-based enum migration refused: shop.Orders.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM txn.PaymentSessions WHERE [Psp] IS NULL OR [Psp] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: txn.PaymentSessions.Psp contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM txn.PaymentSessions WHERE [Status] IS NULL OR [Status] NOT IN (0, 1, 2, 3, 4))
                    THROW 50001, 'One-based enum migration refused: txn.PaymentSessions.Status contains an invalid legacy value.', 1;
                IF EXISTS (SELECT 1 FROM txn.PspConnections WHERE [Psp] IS NULL OR [Psp] NOT IN (0, 1))
                    THROW 50001, 'One-based enum migration refused: txn.PspConnections.Psp contains an invalid legacy value.', 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Roles_ScopeMerchant",
                schema: "iam",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_PaymentSessions_OrderId_Open",
                schema: "txn",
                table: "PaymentSessions");

            // Explicit CASE mappings keep conversion auditable and avoid relying on enum arithmetic.
            migrationBuilder.Sql("""
                UPDATE admin.Sessions SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 WHEN 2 THEN 3 END;
                UPDATE admin.Users SET [Tier] = CASE [Tier] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE admin.Users SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE iam.PermissionGroups SET [Scope] = CASE [Scope] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE iam.PermissionGroups SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE iam.Permissions SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE iam.Roles SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE iam.Roles SET [Scope] = CASE [Scope] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE cfg.Positions SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE cfg.Offices SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE cfg.Levels SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE cfg.Divisions SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE merch.Merchants SET [Status] = CASE [Status] WHEN 0 THEN 1 END;
                UPDATE merch.RegistrationAttempts SET [Purpose] = CASE [Purpose] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE merch.RegistrationAttempts SET [IdentityType] = CASE [IdentityType] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE merch.Sessions SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 WHEN 2 THEN 3 END;
                UPDATE merch.Users SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 WHEN 2 THEN 3 WHEN 3 THEN 4 END;
                UPDATE merch.Users SET [IdentityType] = CASE [IdentityType] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE shop.Orders SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 WHEN 2 THEN 3 WHEN 3 THEN 4 WHEN 4 THEN 5 WHEN 5 THEN 6 END;
                UPDATE txn.PaymentSessions SET [Psp] = CASE [Psp] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                UPDATE txn.PaymentSessions SET [Status] = CASE [Status] WHEN 0 THEN 1 WHEN 1 THEN 2 WHEN 2 THEN 3 WHEN 3 THEN 4 WHEN 4 THEN 5 END;
                UPDATE txn.PspConnections SET [Psp] = CASE [Psp] WHEN 0 THEN 1 WHEN 1 THEN 2 END;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "IdentityType",
                schema: "merch",
                table: "Users",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "IdentityType",
                schema: "merch",
                table: "RegistrationAttempts",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Roles_ScopeMerchant",
                schema: "iam",
                table: "Roles",
                sql: "([Scope] = 1 AND [MerchantId] IS NULL) OR [Scope] = 2");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSessions_OrderId_Open",
                schema: "txn",
                table: "PaymentSessions",
                column: "OrderId",
                unique: true,
                filter: "[Status] IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse rollback rather than silently collapsing values introduced under one-based storage.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM merch.Users WHERE [IdentityType] IS NULL)
                    THROW 50002, 'One-based enum rollback refused: merch.Users.IdentityType contains NULL.', 1;
                IF EXISTS (SELECT 1 FROM merch.RegistrationAttempts WHERE [IdentityType] IS NULL)
                    THROW 50002, 'One-based enum rollback refused: merch.RegistrationAttempts.IdentityType contains NULL.', 1;

                IF EXISTS (SELECT 1 FROM admin.Sessions WHERE [Status] IS NULL OR [Status] NOT IN (1, 2, 3))
                    THROW 50002, 'One-based enum rollback refused: admin.Sessions.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM admin.Users WHERE [Tier] IS NULL OR [Tier] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: admin.Users.Tier contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM admin.Users WHERE [Status] IS NULL OR [Status] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: admin.Users.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM iam.PermissionGroups WHERE [Scope] IS NULL OR [Scope] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: iam.PermissionGroups.Scope contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM iam.PermissionGroups WHERE [Status] IS NULL OR [Status] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: iam.PermissionGroups.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM iam.Permissions WHERE [Status] IS NULL OR [Status] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: iam.Permissions.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM iam.Roles WHERE [Status] IS NULL OR [Status] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: iam.Roles.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM iam.Roles WHERE [Scope] IS NULL OR [Scope] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: iam.Roles.Scope contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM cfg.Positions WHERE [Status] IS NULL OR [Status] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: cfg.Positions.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM cfg.Offices WHERE [Status] IS NULL OR [Status] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: cfg.Offices.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM cfg.Levels WHERE [Status] IS NULL OR [Status] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: cfg.Levels.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM cfg.Divisions WHERE [Status] IS NULL OR [Status] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: cfg.Divisions.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM merch.Merchants WHERE [Status] IS NULL OR [Status] NOT IN (1))
                    THROW 50002, 'One-based enum rollback refused: merch.Merchants.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM merch.RegistrationAttempts WHERE [Purpose] IS NULL OR [Purpose] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: merch.RegistrationAttempts.Purpose contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM merch.RegistrationAttempts WHERE [IdentityType] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: merch.RegistrationAttempts.IdentityType contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM merch.Sessions WHERE [Status] IS NULL OR [Status] NOT IN (1, 2, 3))
                    THROW 50002, 'One-based enum rollback refused: merch.Sessions.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM merch.Users WHERE [Status] IS NULL OR [Status] NOT IN (1, 2, 3, 4))
                    THROW 50002, 'One-based enum rollback refused: merch.Users.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM merch.Users WHERE [IdentityType] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: merch.Users.IdentityType contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM shop.Orders WHERE [Status] IS NULL OR [Status] NOT IN (1, 2, 3, 4, 5, 6))
                    THROW 50002, 'One-based enum rollback refused: shop.Orders.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM txn.PaymentSessions WHERE [Psp] IS NULL OR [Psp] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: txn.PaymentSessions.Psp contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM txn.PaymentSessions WHERE [Status] IS NULL OR [Status] NOT IN (1, 2, 3, 4, 5))
                    THROW 50002, 'One-based enum rollback refused: txn.PaymentSessions.Status contains an invalid value.', 1;
                IF EXISTS (SELECT 1 FROM txn.PspConnections WHERE [Psp] IS NULL OR [Psp] NOT IN (1, 2))
                    THROW 50002, 'One-based enum rollback refused: txn.PspConnections.Psp contains an invalid value.', 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Roles_ScopeMerchant",
                schema: "iam",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_PaymentSessions_OrderId_Open",
                schema: "txn",
                table: "PaymentSessions");

            migrationBuilder.Sql("""
                UPDATE admin.Sessions SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 WHEN 3 THEN 2 END;
                UPDATE admin.Users SET [Tier] = CASE [Tier] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE admin.Users SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE iam.PermissionGroups SET [Scope] = CASE [Scope] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE iam.PermissionGroups SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE iam.Permissions SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE iam.Roles SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE iam.Roles SET [Scope] = CASE [Scope] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE cfg.Positions SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE cfg.Offices SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE cfg.Levels SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE cfg.Divisions SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE merch.Merchants SET [Status] = CASE [Status] WHEN 1 THEN 0 END;
                UPDATE merch.RegistrationAttempts SET [Purpose] = CASE [Purpose] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE merch.RegistrationAttempts SET [IdentityType] = CASE [IdentityType] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE merch.Sessions SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 WHEN 3 THEN 2 END;
                UPDATE merch.Users SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 WHEN 3 THEN 2 WHEN 4 THEN 3 END;
                UPDATE merch.Users SET [IdentityType] = CASE [IdentityType] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE shop.Orders SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 WHEN 3 THEN 2 WHEN 4 THEN 3 WHEN 5 THEN 4 WHEN 6 THEN 5 END;
                UPDATE txn.PaymentSessions SET [Psp] = CASE [Psp] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                UPDATE txn.PaymentSessions SET [Status] = CASE [Status] WHEN 1 THEN 0 WHEN 2 THEN 1 WHEN 3 THEN 2 WHEN 4 THEN 3 WHEN 5 THEN 4 END;
                UPDATE txn.PspConnections SET [Psp] = CASE [Psp] WHEN 1 THEN 0 WHEN 2 THEN 1 END;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "IdentityType",
                schema: "merch",
                table: "Users",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<int>(
                name: "IdentityType",
                schema: "merch",
                table: "RegistrationAttempts",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Roles_ScopeMerchant",
                schema: "iam",
                table: "Roles",
                sql: "([Scope] = 0 AND [MerchantId] IS NULL) OR [Scope] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSessions_OrderId_Open",
                schema: "txn",
                table: "PaymentSessions",
                column: "OrderId",
                unique: true,
                filter: "[Status] IN (0, 1)");
        }
    }
}
