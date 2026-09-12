using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task4AgentRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Originators_MerchantId_SaleId",
                schema: "acct",
                table: "Agents");

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'acct.AgentRegistrations', N'U') IS NULL
                BEGIN
                    CREATE TABLE [acct].[AgentRegistrations](
                        [Id] uniqueidentifier NOT NULL,
                        [MerchantId] uniqueidentifier NOT NULL,
                        [Provider] nvarchar(64) NOT NULL,
                        [TenantId] nvarchar(128) NOT NULL,
                        [ExternalUserId] nvarchar(256) NOT NULL,
                        [CurrentAttemptId] uniqueidentifier NULL,
                        [CurrentAttemptNo] int NOT NULL,
                        [Status] int NOT NULL,
                        [SaleCode] nvarchar(64) NOT NULL,
                        [Email] nvarchar(320) NOT NULL,
                        [PhoneNumber] nvarchar(64) NOT NULL,
                        [ProfileJson] json NOT NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        [UpdatedAt] datetime2 NOT NULL,
                        [Version] bigint NOT NULL,
                        CONSTRAINT [PK_AgentRegistrations] PRIMARY KEY ([Id])
                    );
                    CREATE UNIQUE INDEX [IX_AgentRegistrations_Provider_TenantId_ExternalUserId]
                        ON [acct].[AgentRegistrations]([Provider], [TenantId], [ExternalUserId]);
                    CREATE INDEX [IX_AgentRegistrations_MerchantId_Status_UpdatedAt]
                        ON [acct].[AgentRegistrations]([MerchantId], [Status], [UpdatedAt]);
                END;

                IF OBJECT_ID(N'acct.AgentRegistrationAttempts', N'U') IS NULL
                BEGIN
                    CREATE TABLE [acct].[AgentRegistrationAttempts](
                        [Id] uniqueidentifier NOT NULL,
                        [RegistrationId] uniqueidentifier NOT NULL,
                        [MerchantId] uniqueidentifier NOT NULL,
                        [AttemptNo] int NOT NULL,
                        [Provider] nvarchar(64) NOT NULL,
                        [TenantId] nvarchar(128) NOT NULL,
                        [ExternalUserId] nvarchar(256) NOT NULL,
                        [SaleCode] nvarchar(64) NOT NULL,
                        [SaleId] uniqueidentifier NOT NULL,
                        [BranchId] uniqueidentifier NOT NULL,
                        [SaleVersion] bigint NOT NULL,
                        [BranchVersion] bigint NOT NULL,
                        [Email] nvarchar(320) NOT NULL,
                        [PhoneNumber] nvarchar(64) NOT NULL,
                        [ProfileJson] json NOT NULL,
                        [IdempotencyKey] nvarchar(200) NOT NULL,
                        [IntentHash] varchar(64) NOT NULL,
                        [Status] int NOT NULL,
                        [SubmittedAt] datetime2 NOT NULL,
                        [DecidedAt] datetime2 NULL,
                        [DecidedByAccountId] uniqueidentifier NULL,
                        [RejectionReason] nvarchar(1000) NULL,
                        [InternalReviewNote] nvarchar(4000) NULL,
                        [ContactEvidenceReference] nvarchar(256) NULL,
                        [ContactVerifiedByAccountId] uniqueidentifier NULL,
                        [ContactVerifiedAt] datetime2 NULL,
                        [DecisionIdempotencyKey] nvarchar(200) NULL,
                        [DecisionIntentHash] varchar(64) NULL,
                        [Version] bigint NOT NULL,
                        CONSTRAINT [PK_AgentRegistrationAttempts] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_AgentRegistrationAttempts_AgentRegistrations_RegistrationId]
                            FOREIGN KEY ([RegistrationId]) REFERENCES [acct].[AgentRegistrations]([Id]) ON DELETE NO ACTION
                    );
                    CREATE UNIQUE INDEX [IX_AgentRegistrationAttempts_RegistrationId_AttemptNo]
                        ON [acct].[AgentRegistrationAttempts]([RegistrationId], [AttemptNo]);
                    CREATE UNIQUE INDEX [IX_AgentRegistrationAttempts_RegistrationId_IdempotencyKey]
                        ON [acct].[AgentRegistrationAttempts]([RegistrationId], [IdempotencyKey]);
                    CREATE INDEX [IX_AgentRegistrationAttempts_MerchantId_Status_SubmittedAt]
                        ON [acct].[AgentRegistrationAttempts]([MerchantId], [Status], [SubmittedAt]);
                END;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Sales_MerchantId_SaleId",
                schema: "acct",
                table: "Agents",
                columns: new[] { "MerchantId", "SaleId" },
                principalSchema: "merch",
                principalTable: "Sales",
                principalColumns: new[] { "MerchantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Sales_MerchantId_SaleId",
                schema: "acct",
                table: "Agents");

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'acct.AgentRegistrationAttempts', N'U') IS NOT NULL
                    DROP TABLE [acct].[AgentRegistrationAttempts];
                IF OBJECT_ID(N'acct.AgentRegistrations', N'U') IS NOT NULL
                    DROP TABLE [acct].[AgentRegistrations];
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Originators_MerchantId_SaleId",
                schema: "acct",
                table: "Agents",
                columns: new[] { "MerchantId", "SaleId" },
                principalSchema: "merch",
                principalTable: "Originators",
                principalColumns: new[] { "MerchantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
