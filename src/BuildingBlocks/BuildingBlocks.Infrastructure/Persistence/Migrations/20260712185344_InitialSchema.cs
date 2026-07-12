using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "admin");

            migrationBuilder.EnsureSchema(
                name: "merch");

            migrationBuilder.EnsureSchema(
                name: "shop");

            migrationBuilder.EnsureSchema(
                name: "dbo");

            migrationBuilder.EnsureSchema(
                name: "txn");

            migrationBuilder.EnsureSchema(
                name: "iam");

            migrationBuilder.CreateTable(
                name: "AuthAudits",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PlatformUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthAudits",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MerchantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Carts",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Carts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CheckoutSessions",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CartId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NotificationRecipient = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    AmountAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    AmountCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckoutSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FriendlyName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Xml = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Divisions",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Divisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExternalLogins",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    MerchantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalLogins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                schema: "txn",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Context = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Levels",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Levels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MerchantAccess",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlatformUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedByAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantAccess", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Merchants",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LegalEntityId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Country = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    EnabledChannels = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Merchants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Offices",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Offices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CheckoutSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SummaryToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SummaryTokenExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NotificationRecipient = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    AmountAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    AmountCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentSessions",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Psp = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PspExternalChargeId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RedirectUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    AmountAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    AmountCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionGroups",
                schema: "iam",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    LabelTh = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionGroups", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PriceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    PriceCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProvisioningAudits",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AdminSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PspConnections",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Psp = table.Column<int>(type: "int", nullable: false),
                    EnabledMethods = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SecretRefName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PspConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationAudits",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TargetSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "iam",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Color = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                    table.CheckConstraint("CK_Roles_ScopeMerchant", "([Scope] = 0 AND [MerchantId] IS NULL) OR [Scope] = 1");
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    PlatformUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IdleExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SupersededAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SupersededBySessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    MerchantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IdleExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SupersededAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SupersededBySessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserAudits",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PersonType = table.Column<int>(type: "int", nullable: true),
                    IdNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProducerCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LicenseNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PhotoObjectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PhotoContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VaultRevealAudits",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecretName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    PrevHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Hash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    RevealedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultRevealAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VaultSecrets",
                schema: "merch",
                columns: table => new
                {
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    KeyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EncryptedDek = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    EncryptedSecret = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Hint = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultSecrets", x => new { x.MerchantId, x.Name });
                });

            migrationBuilder.CreateTable(
                name: "CartItems",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CartId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPriceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    UnitPriceCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CartItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CartItems_Carts_CartId",
                        column: x => x.CartId,
                        principalSchema: "shop",
                        principalTable: "Carts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Permissions",
                schema: "iam",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GroupKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LabelTh = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Key);
                    table.ForeignKey(
                        name: "FK_Permissions_PermissionGroups_GroupKey",
                        column: x => x.GroupKey,
                        principalSchema: "iam",
                        principalTable: "PermissionGroups",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Tier = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PositionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OfficeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LevelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DivisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Divisions_DivisionId",
                        column: x => x.DivisionId,
                        principalSchema: "admin",
                        principalTable: "Divisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Users_Levels_LevelId",
                        column: x => x.LevelId,
                        principalSchema: "admin",
                        principalTable: "Levels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Users_Offices_OfficeId",
                        column: x => x.OfficeId,
                        principalSchema: "admin",
                        principalTable: "Offices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Users_Positions_PositionId",
                        column: x => x.PositionId,
                        principalSchema: "admin",
                        principalTable: "Positions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlatformUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "iam",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "iam",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                schema: "iam",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermissionKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Permissions_PermissionKey",
                        column: x => x.PermissionKey,
                        principalSchema: "iam",
                        principalTable: "Permissions",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "iam",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthAudits_PlatformUserId",
                schema: "admin",
                table: "AuthAudits",
                column: "PlatformUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthAudits_MerchantUserId",
                schema: "merch",
                table: "AuthAudits",
                column: "MerchantUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CartItems_CartId",
                schema: "shop",
                table: "CartItems",
                column: "CartId");

            migrationBuilder.CreateIndex(
                name: "IX_Divisions_Code",
                schema: "admin",
                table: "Divisions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLogins_Provider_Subject",
                schema: "merch",
                table: "ExternalLogins",
                columns: new[] { "Provider", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Levels_Code",
                schema: "admin",
                table: "Levels",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantAccess_PlatformUserId_MerchantId",
                schema: "admin",
                table: "MerchantAccess",
                columns: new[] { "PlatformUserId", "MerchantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Merchants_Code",
                schema: "merch",
                table: "Merchants",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Offices_Code",
                schema: "admin",
                table: "Offices",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CheckoutSessionId",
                schema: "shop",
                table: "Orders",
                column: "CheckoutSessionId",
                unique: true,
                filter: "[CheckoutSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_MerchantId",
                schema: "shop",
                table: "Orders",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PaymentSessionId",
                schema: "shop",
                table: "Orders",
                column: "PaymentSessionId",
                filter: "[PaymentSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SummaryToken",
                schema: "shop",
                table: "Orders",
                column: "SummaryToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt_LeaseExpiresAt",
                schema: "txn",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSessions_OrderId",
                schema: "txn",
                table: "PaymentSessions",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSessions_Psp_PspExternalChargeId",
                schema: "txn",
                table: "PaymentSessions",
                columns: new[] { "Psp", "PspExternalChargeId" },
                unique: true,
                filter: "[PspExternalChargeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_GroupKey",
                schema: "iam",
                table: "Permissions",
                column: "GroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Code",
                schema: "admin",
                table: "Positions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_MerchantId_IsActive",
                schema: "shop",
                table: "Products",
                columns: new[] { "MerchantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_PspConnections_MerchantId_Psp",
                schema: "txn",
                table: "PspConnections",
                columns: new[] { "MerchantId", "Psp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_PlatformUserId_RoleId",
                schema: "admin",
                table: "RoleAssignments",
                columns: new[] { "PlatformUserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_RoleId",
                schema: "admin",
                table: "RoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_MerchantUserId_MerchantId",
                schema: "merch",
                table: "RoleAssignments",
                columns: new[] { "MerchantUserId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_MerchantUserId_RoleId",
                schema: "merch",
                table: "RoleAssignments",
                columns: new[] { "MerchantUserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_RoleId",
                schema: "merch",
                table: "RoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_PermissionKey",
                schema: "iam",
                table: "RolePermissions",
                column: "PermissionKey");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleId_PermissionKey",
                schema: "iam",
                table: "RolePermissions",
                columns: new[] { "RoleId", "PermissionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_MerchantId_Code",
                schema: "iam",
                table: "Roles",
                columns: new[] { "MerchantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_AbsoluteExpiresAt",
                schema: "admin",
                table: "Sessions",
                column: "AbsoluteExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_FamilyId",
                schema: "admin",
                table: "Sessions",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_PlatformUserId",
                schema: "admin",
                table: "Sessions",
                column: "PlatformUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TokenHash",
                schema: "admin",
                table: "Sessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_AbsoluteExpiresAt",
                schema: "merch",
                table: "Sessions",
                column: "AbsoluteExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_FamilyId",
                schema: "merch",
                table: "Sessions",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_MerchantUserId",
                schema: "merch",
                table: "Sessions",
                column: "MerchantUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TokenHash",
                schema: "merch",
                table: "Sessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_DivisionId",
                schema: "admin",
                table: "Users",
                column: "DivisionId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                schema: "admin",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_LevelId",
                schema: "admin",
                table: "Users",
                column: "LevelId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_OfficeId",
                schema: "admin",
                table: "Users",
                column: "OfficeId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_PositionId",
                schema: "admin",
                table: "Users",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Subject",
                schema: "admin",
                table: "Users",
                column: "Subject",
                unique: true,
                filter: "[Subject] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Subject",
                schema: "merch",
                table: "Users",
                column: "Subject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultRevealAudits_MerchantId_Id",
                schema: "merch",
                table: "VaultRevealAudits",
                columns: new[] { "MerchantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultRevealAudits_MerchantId_Seq",
                schema: "merch",
                table: "VaultRevealAudits",
                columns: new[] { "MerchantId", "Seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthAudits",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "AuthAudits",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "CartItems",
                schema: "shop");

            migrationBuilder.DropTable(
                name: "CheckoutSessions",
                schema: "shop");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ExternalLogins",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "MerchantAccess",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Merchants",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "Orders",
                schema: "shop");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "PaymentSessions",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "Products",
                schema: "shop");

            migrationBuilder.DropTable(
                name: "ProvisioningAudits",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "PspConnections",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "RegistrationAudits",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "RoleAssignments",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "RoleAssignments",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "RolePermissions",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "Sessions",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Sessions",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "UserAudits",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "VaultRevealAudits",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "VaultSecrets",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "Carts",
                schema: "shop");

            migrationBuilder.DropTable(
                name: "Permissions",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "Divisions",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Levels",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Offices",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Positions",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "PermissionGroups",
                schema: "iam");
        }
    }
}
