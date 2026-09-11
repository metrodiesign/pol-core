using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol.Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task2IdentityAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "access");

            migrationBuilder.EnsureSchema(
                name: "acct");

            migrationBuilder.EnsureSchema(
                name: "oauth");

            migrationBuilder.CreateTable(
                name: "AccessRoles",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantAccessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Accounts",
                schema: "acct",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountType = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.CheckConstraint("CK_Accounts_AccountType", "[AccountType] IN (1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                schema: "acct",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Metadata = table.Column<string>(type: "json", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "AssertionReplays",
                schema: "oauth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Jti = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssertionReplays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BffSessionTickets",
                schema: "acct",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketKeyHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ProtectedAuthenticationTicket = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BffSessionTickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BranchAccess",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantAccessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchAccess", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientKeyPolicies",
                schema: "acct",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SystemClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    KeyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Algorithm = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AuditReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientKeyPolicies", x => x.Id);
                    table.CheckConstraint("CK_ClientKeyPolicies_Validity", "[ValidUntil] IS NULL OR [ValidUntil] > [ValidFrom]");
                });

            migrationBuilder.CreateTable(
                name: "Employees",
                schema: "acct",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DepartmentCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Metadata = table.Column<string>(type: "json", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "LoginAccounts",
                schema: "acct",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExternalUserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MerchantAccess",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataScope = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantAccess", x => x.Id);
                    table.CheckConstraint("CK_MerchantAccess_DataScope", "[DataScope] IN (1, 2, 3, 4)");
                });

            migrationBuilder.CreateTable(
                name: "MerchantAccessMethods",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantAccessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MethodCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantAccessMethods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictApplications",
                schema: "oauth",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ApplicationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClientSecret = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClientType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ConsentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JsonWebKeySet = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Permissions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PostLogoutRedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Requirements = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Settings = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictScopes",
                schema: "oauth",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Descriptions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Resources = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAccess",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAccess", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAccessRoles",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlatformAccessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAccessRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationSessions",
                schema: "acct",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionReferenceHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExternalUserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemClients",
                schema: "acct",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AllowedGrantTypes = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemClients", x => x.Id);
                    table.CheckConstraint("CK_SystemClients_GrantTypes", "[AllowedGrantTypes] = 'client_credentials'");
                });

            migrationBuilder.CreateTable(
                name: "SystemClientScopes",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SystemClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemClientScopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictAuthorizations",
                schema: "oauth",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ApplicationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictAuthorizations_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "oauth",
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictTokens",
                schema: "oauth",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ApplicationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    AuthorizationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RedemptionDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "oauth",
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalSchema: "oauth",
                        principalTable: "OpenIddictAuthorizations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoles_MerchantAccessId_RoleId",
                schema: "access",
                table: "AccessRoles",
                columns: new[] { "MerchantAccessId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Status",
                schema: "acct",
                table: "Accounts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_SaleId",
                schema: "acct",
                table: "Agents",
                column: "SaleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssertionReplays_ApplicationId_Jti",
                schema: "oauth",
                table: "AssertionReplays",
                columns: new[] { "ApplicationId", "Jti" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssertionReplays_ExpiresAt",
                schema: "oauth",
                table: "AssertionReplays",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_BffSessionTickets_AccountId_RevokedAt",
                schema: "acct",
                table: "BffSessionTickets",
                columns: new[] { "AccountId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BffSessionTickets_TicketKeyHash",
                schema: "acct",
                table: "BffSessionTickets",
                column: "TicketKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BranchAccess_MerchantAccessId_BranchId",
                schema: "access",
                table: "BranchAccess",
                columns: new[] { "MerchantAccessId", "BranchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientKeyPolicies_ApplicationId_KeyId",
                schema: "acct",
                table: "ClientKeyPolicies",
                columns: new[] { "ApplicationId", "KeyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientKeyPolicies_SystemClientId_Status",
                schema: "acct",
                table: "ClientKeyPolicies",
                columns: new[] { "SystemClientId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_EmployeeCode",
                schema: "acct",
                table: "Employees",
                column: "EmployeeCode",
                unique: true,
                filter: "[EmployeeCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LoginAccounts_AccountId",
                schema: "acct",
                table: "LoginAccounts",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginAccounts_Provider_TenantId_ExternalUserId",
                schema: "acct",
                table: "LoginAccounts",
                columns: new[] { "Provider", "TenantId", "ExternalUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantAccess_AccountId_MerchantId",
                schema: "access",
                table: "MerchantAccess",
                columns: new[] { "AccountId", "MerchantId" },
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantAccess_MerchantId",
                schema: "access",
                table: "MerchantAccess",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantAccessMethods_MerchantAccessId_MethodCode",
                schema: "access",
                table: "MerchantAccessMethods",
                columns: new[] { "MerchantAccessId", "MethodCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictApplications_ClientId",
                schema: "oauth",
                table: "OpenIddictApplications",
                column: "ClientId",
                unique: true,
                filter: "[ClientId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type",
                schema: "oauth",
                table: "OpenIddictAuthorizations",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictScopes_Name",
                schema: "oauth",
                table: "OpenIddictScopes",
                column: "Name",
                unique: true,
                filter: "[Name] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type",
                schema: "oauth",
                table: "OpenIddictTokens",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_AuthorizationId",
                schema: "oauth",
                table: "OpenIddictTokens",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ReferenceId",
                schema: "oauth",
                table: "OpenIddictTokens",
                column: "ReferenceId",
                unique: true,
                filter: "[ReferenceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAccess_EmployeeAccountId",
                schema: "access",
                table: "PlatformAccess",
                column: "EmployeeAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAccessRoles_PlatformAccessId_RoleId",
                schema: "access",
                table: "PlatformAccessRoles",
                columns: new[] { "PlatformAccessId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationSessions_Provider_TenantId_ExternalUserId_MerchantId",
                schema: "acct",
                table: "RegistrationSessions",
                columns: new[] { "Provider", "TenantId", "ExternalUserId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationSessions_SessionReferenceHash",
                schema: "acct",
                table: "RegistrationSessions",
                column: "SessionReferenceHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemClients_AccountId",
                schema: "acct",
                table: "SystemClients",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemClients_ClientId",
                schema: "acct",
                table: "SystemClients",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemClients_MerchantId_Environment",
                schema: "acct",
                table: "SystemClients",
                columns: new[] { "MerchantId", "Environment" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemClientScopes_SystemClientId_ScopeCode",
                schema: "access",
                table: "SystemClientScopes",
                columns: new[] { "SystemClientId", "ScopeCode" },
                unique: true);

            // The runtime uses the single pol_app principal. Keep the new control-plane schemas behind
            // the same explicit grant floor as the existing runtime tables.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON acct.Accounts TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON acct.LoginAccounts TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON acct.Employees TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON acct.Agents TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON acct.SystemClients TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON acct.ClientKeyPolicies TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON acct.BffSessionTickets TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON acct.RegistrationSessions TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON access.MerchantAccess TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON access.AccessRoles TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON access.BranchAccess TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON access.PlatformAccess TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON access.PlatformAccessRoles TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON access.SystemClientScopes TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON access.MerchantAccessMethods TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON oauth.AssertionReplays TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON oauth.OpenIddictApplications TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON oauth.OpenIddictAuthorizations TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON oauth.OpenIddictScopes TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON oauth.OpenIddictTokens TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessRoles",
                schema: "access");

            migrationBuilder.DropTable(
                name: "Accounts",
                schema: "acct");

            migrationBuilder.DropTable(
                name: "Agents",
                schema: "acct");

            migrationBuilder.DropTable(
                name: "AssertionReplays",
                schema: "oauth");

            migrationBuilder.DropTable(
                name: "BffSessionTickets",
                schema: "acct");

            migrationBuilder.DropTable(
                name: "BranchAccess",
                schema: "access");

            migrationBuilder.DropTable(
                name: "ClientKeyPolicies",
                schema: "acct");

            migrationBuilder.DropTable(
                name: "Employees",
                schema: "acct");

            migrationBuilder.DropTable(
                name: "LoginAccounts",
                schema: "acct");

            migrationBuilder.DropTable(
                name: "MerchantAccess",
                schema: "access");

            migrationBuilder.DropTable(
                name: "MerchantAccessMethods",
                schema: "access");

            migrationBuilder.DropTable(
                name: "OpenIddictScopes",
                schema: "oauth");

            migrationBuilder.DropTable(
                name: "OpenIddictTokens",
                schema: "oauth");

            migrationBuilder.DropTable(
                name: "PlatformAccess",
                schema: "access");

            migrationBuilder.DropTable(
                name: "PlatformAccessRoles",
                schema: "access");

            migrationBuilder.DropTable(
                name: "RegistrationSessions",
                schema: "acct");

            migrationBuilder.DropTable(
                name: "SystemClients",
                schema: "acct");

            migrationBuilder.DropTable(
                name: "SystemClientScopes",
                schema: "access");

            migrationBuilder.DropTable(
                name: "OpenIddictAuthorizations",
                schema: "oauth");

            migrationBuilder.DropTable(
                name: "OpenIddictApplications",
                schema: "oauth");
        }
    }
}
