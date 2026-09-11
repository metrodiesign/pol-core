using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol.Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations;

public partial class Task9MigrationReadiness : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("IF OBJECT_ID(N'cfg.MigrationRuns', N'U') IS NULL BEGIN CREATE TABLE cfg.MigrationRuns (RunId uniqueidentifier NOT NULL CONSTRAINT PK_MigrationRuns PRIMARY KEY, SourceSnapshotId nvarchar(256) NOT NULL, Status int NOT NULL, ConflictFingerprint varchar(64) NOT NULL, InvariantsJson nvarchar(max) NOT NULL, CapturedAt datetime2 NOT NULL, ExternalCallCount int NOT NULL); END;");
        migrationBuilder.Sql("IF OBJECT_ID(N'cfg.LegacyIdentityMaps', N'U') IS NULL BEGIN CREATE TABLE cfg.LegacyIdentityMaps (RunId uniqueidentifier NOT NULL, LegacyKind nvarchar(64) NOT NULL, LegacyId nvarchar(200) NOT NULL, AccountId uniqueidentifier NOT NULL, MerchantId uniqueidentifier NOT NULL, EvidenceReference nvarchar(256) NOT NULL, MigratedAt datetime2 NOT NULL, CONSTRAINT PK_LegacyIdentityMaps PRIMARY KEY (RunId, LegacyKind, LegacyId), CONSTRAINT UQ_LegacyIdentityMaps_Run_Account UNIQUE (RunId, AccountId), CONSTRAINT FK_LegacyIdentityMaps_Run FOREIGN KEY (RunId) REFERENCES cfg.MigrationRuns(RunId)); END;");
        migrationBuilder.Sql("IF OBJECT_ID(N'cfg.MigrationConflicts', N'U') IS NULL BEGIN CREATE TABLE cfg.MigrationConflicts (Id uniqueidentifier NOT NULL CONSTRAINT PK_MigrationConflicts PRIMARY KEY, RunId uniqueidentifier NOT NULL, EntityKind nvarchar(64) NOT NULL, EntityId nvarchar(200) NOT NULL, ReasonCode int NOT NULL, SafeDetails nvarchar(512) NOT NULL, ResolutionStatus nvarchar(32) NOT NULL CONSTRAINT DF_MigrationConflicts_Status DEFAULT N'UNRESOLVED', CONSTRAINT FK_MigrationConflicts_Run FOREIGN KEY (RunId) REFERENCES cfg.MigrationRuns(RunId)); CREATE UNIQUE INDEX IX_MigrationConflicts_Run_Entity_Reason ON cfg.MigrationConflicts(RunId, EntityKind, EntityId, ReasonCode); END;");
        migrationBuilder.Sql("IF OBJECT_ID(N'cfg.MigratedTransactions', N'U') IS NULL BEGIN CREATE TABLE cfg.MigratedTransactions (RunId uniqueidentifier NOT NULL, TransactionId uniqueidentifier NOT NULL, OrderId uniqueidentifier NOT NULL, MerchantId uniqueidentifier NOT NULL, ProviderAccountReference nvarchar(256) NOT NULL, Environment nvarchar(32) NOT NULL, PspRequestReference nvarchar(256) NOT NULL, PspTransactionReference nvarchar(256) NULL, Amount decimal(19,4) NOT NULL, Currency char(3) NOT NULL, Status nvarchar(64) NOT NULL, HistoryJson nvarchar(max) NOT NULL, SnapshotJson nvarchar(max) NOT NULL, Provenance nvarchar(64) NOT NULL, CONSTRAINT PK_MigratedTransactions PRIMARY KEY (RunId, TransactionId), CONSTRAINT FK_MigratedTransactions_Run FOREIGN KEY (RunId) REFERENCES cfg.MigrationRuns(RunId)); END;");
        migrationBuilder.Sql("IF OBJECT_ID(N'cfg.MigrationRecoveryInbox', N'U') IS NULL BEGIN CREATE TABLE cfg.MigrationRecoveryInbox (RunId uniqueidentifier NOT NULL, Sequence bigint NOT NULL, CallbackId nvarchar(200) NOT NULL, ProviderReference nvarchar(256) NOT NULL, ReceivedAt datetime2 NOT NULL, Payload nvarchar(max) NOT NULL, Status int NOT NULL, ReplayedAt datetime2 NULL, CONSTRAINT PK_MigrationRecoveryInbox PRIMARY KEY (RunId, Sequence), CONSTRAINT UQ_MigrationRecoveryInbox_Run_Callback UNIQUE (RunId, CallbackId), CONSTRAINT FK_MigrationRecoveryInbox_Run FOREIGN KEY (RunId) REFERENCES cfg.MigrationRuns(RunId)); END;");
        migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app') BEGIN GRANT SELECT, INSERT, UPDATE ON cfg.MigrationRuns TO pol_app; GRANT SELECT, INSERT, UPDATE ON cfg.LegacyIdentityMaps TO pol_app; GRANT SELECT, INSERT, UPDATE ON cfg.MigrationConflicts TO pol_app; GRANT SELECT, INSERT, UPDATE ON cfg.MigratedTransactions TO pol_app; GRANT SELECT, INSERT, UPDATE ON cfg.MigrationRecoveryInbox TO pol_app; END;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pol_app') BEGIN REVOKE SELECT, INSERT, UPDATE ON cfg.MigrationRecoveryInbox FROM pol_app; REVOKE SELECT, INSERT, UPDATE ON cfg.MigratedTransactions FROM pol_app; REVOKE SELECT, INSERT, UPDATE ON cfg.MigrationConflicts FROM pol_app; REVOKE SELECT, INSERT, UPDATE ON cfg.LegacyIdentityMaps FROM pol_app; REVOKE SELECT, INSERT, UPDATE ON cfg.MigrationRuns FROM pol_app; END;");
        migrationBuilder.Sql("IF OBJECT_ID(N'cfg.MigrationRecoveryInbox', N'U') IS NOT NULL DROP TABLE cfg.MigrationRecoveryInbox;");
        migrationBuilder.Sql("IF OBJECT_ID(N'cfg.MigratedTransactions', N'U') IS NOT NULL DROP TABLE cfg.MigratedTransactions;");
        migrationBuilder.Sql("IF OBJECT_ID(N'cfg.MigrationConflicts', N'U') IS NOT NULL DROP TABLE cfg.MigrationConflicts;");
        migrationBuilder.Sql("IF OBJECT_ID(N'cfg.LegacyIdentityMaps', N'U') IS NOT NULL DROP TABLE cfg.LegacyIdentityMaps;");
        migrationBuilder.Sql("IF OBJECT_ID(N'cfg.MigrationRuns', N'U') IS NOT NULL DROP TABLE cfg.MigrationRuns;");
    }
}
