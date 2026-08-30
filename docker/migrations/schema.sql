IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    DECLARE @target sysname = DB_NAME();
    DECLARE @major int = TRY_CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));
    DECLARE @version nvarchar(128) = CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));
    DECLARE @build int = TRY_CONVERT(int, PARSENAME(@version, 2));
    DECLARE @revision int = TRY_CONVERT(int, PARSENAME(@version, 1));
    DECLARE @message nvarchar(2048);

    IF @major <> 17 OR @build < 4045 OR (@build = 4045 AND @revision < 5)
    BEGIN
        SET @message = CONCAT(N'InitialSchema refused target database [', @target,
            N']: SQL Server 2025 CU5 (17.0.4045.5) or newer is required.');
        THROW 51000, @message, 1;
    END

    IF (SELECT compatibility_level FROM sys.databases WHERE name = @target) <> 170
    BEGIN
        SET @message = CONCAT(N'InitialSchema refused target database [', @target,
            N']: compatibility level 170 is required.');
        THROW 51001, @message, 1;
    END

    IF EXISTS (SELECT 1 FROM sys.schemas
               WHERE name IN (N'admin', N'cfg', N'iam', N'merch', N'shop', N'txn'))
       OR EXISTS (SELECT 1 FROM sys.tables WHERE name <> N'__EFMigrationsHistory' AND is_ms_shipped = 0)
       OR EXISTS (SELECT 1 FROM sys.views WHERE is_ms_shipped = 0)
       OR EXISTS (SELECT 1 FROM sys.procedures WHERE is_ms_shipped = 0)
       OR (OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NOT NULL
           AND EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory))
    BEGIN
        SET @message = CONCAT(N'InitialSchema refused non-empty or legacy target database [', @target, N'].');
        THROW 51002, @message, 1;
    END
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    IF SCHEMA_ID(N'admin') IS NULL EXEC(N'CREATE SCHEMA [admin];');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    IF SCHEMA_ID(N'merch') IS NULL EXEC(N'CREATE SCHEMA [merch];');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    IF SCHEMA_ID(N'shop') IS NULL EXEC(N'CREATE SCHEMA [shop];');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    IF SCHEMA_ID(N'cfg') IS NULL EXEC(N'CREATE SCHEMA [cfg];');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    IF SCHEMA_ID(N'txn') IS NULL EXEC(N'CREATE SCHEMA [txn];');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    IF SCHEMA_ID(N'iam') IS NULL EXEC(N'CREATE SCHEMA [iam];');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [admin].[AuthAudits] (
        [Id] uniqueidentifier NOT NULL,
        [EventType] nvarchar(32) NOT NULL,
        [AdminUserId] uniqueidentifier NULL,
        [Subject] nvarchar(256) NULL,
        [Reason] nvarchar(128) NULL,
        [CorrelationId] nvarchar(128) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AuthAudits] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[AuthAudits] (
        [Id] uniqueidentifier NOT NULL,
        [EventType] nvarchar(32) NOT NULL,
        [UserId] uniqueidentifier NULL,
        [Subject] nvarchar(256) NULL,
        [Reason] nvarchar(128) NULL,
        [CorrelationId] nvarchar(128) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AuthAudits] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [shop].[Carts] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [SaleCode] varchar(20) NULL,
        [Status] nvarchar(16) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [Version] int NOT NULL,
        CONSTRAINT [PK_Carts] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_Carts_Id_MerchantId] UNIQUE ([Id], [MerchantId])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [dbo].[DataProtectionKeys] (
        [Id] int NOT NULL IDENTITY,
        [SecretKey] nvarchar(256) NULL,
        [Xml] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_DataProtectionKeys] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [cfg].[Divisions] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(64) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Status] int NOT NULL,
        CONSTRAINT [PK_Divisions] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[ExternalLogins] (
        [Id] uniqueidentifier NOT NULL,
        [Provider] nvarchar(32) NOT NULL,
        [Subject] nvarchar(256) NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ExternalLogins] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [txn].[IdempotencyRecords] (
        [Key] nvarchar(400) NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Context] nvarchar(256) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_IdempotencyRecords] PRIMARY KEY ([Key])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [cfg].[Levels] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(64) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Status] int NOT NULL,
        CONSTRAINT [PK_Levels] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [admin].[MerchantAccess] (
        [Id] uniqueidentifier NOT NULL,
        [AdminUserId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [AssignedByAdminId] uniqueidentifier NOT NULL,
        [AssignedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_MerchantAccess] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[Merchants] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(64) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Note] nvarchar(max) NULL,
        [Status] int NOT NULL,
        [Country] nvarchar(2) NOT NULL,
        [Currency] nvarchar(3) NOT NULL,
        [EnabledChannels] nvarchar(256) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [Metadata] json NOT NULL,
        CONSTRAINT [PK_Merchants] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [cfg].[Offices] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(64) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Status] int NOT NULL,
        CONSTRAINT [PK_Offices] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [shop].[OrderItemRevealAudits] (
        [Id] uniqueidentifier NOT NULL,
        [OrderItemId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [ActorType] nvarchar(32) NOT NULL,
        [ActorId] nvarchar(200) NOT NULL,
        [CorrelationId] nvarchar(200) NOT NULL,
        [RevealedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_OrderItemRevealAudits] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [shop].[Orders] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [OrderNo] varchar(13) NOT NULL,
        [SaleCode] varchar(20) NULL,
        [PaymentSessionId] uniqueidentifier NULL,
        [Status] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [PaidAt] datetime2 NULL,
        [SummaryToken] nvarchar(64) NOT NULL,
        [SummaryTokenExpiresAt] datetime2 NOT NULL,
        [NotificationRecipient] nvarchar(320) NULL,
        [PaymentChannel] varchar(20) NULL,
        [CustomerName] nvarchar(200) NOT NULL,
        [CustomerPhone] varchar(20) NOT NULL,
        [CustomerEmail] nvarchar(320) NULL,
        [AmountAmount] decimal(19,4) NOT NULL,
        [AmountCurrency] char(3) NOT NULL,
        CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_Orders_Id_MerchantId] UNIQUE ([Id], [MerchantId])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [txn].[OutboxMessages] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Type] nvarchar(256) NOT NULL,
        [SchemaVersion] varchar(16) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        [ProcessedAt] datetime2 NULL,
        [Attempts] int NOT NULL,
        [Error] nvarchar(2048) NULL,
        [LeaseExpiresAt] datetime2 NULL,
        [LeaseOwner] nvarchar(256) NULL,
        CONSTRAINT [PK_OutboxMessages] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [txn].[PaymentSessions] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [Method] nvarchar(32) NOT NULL,
        [Psp] int NOT NULL,
        [Status] int NOT NULL,
        [PspExternalChargeId] nvarchar(256) NULL,
        [RedirectUrl] nvarchar(2048) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [RowVersion] rowversion NOT NULL,
        [AmountAmount] decimal(19,4) NOT NULL,
        [AmountCurrency] char(3) NOT NULL,
        CONSTRAINT [PK_PaymentSessions] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [iam].[PermissionGroups] (
        [Key] nvarchar(32) NOT NULL,
        [Scope] int NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [Status] int NOT NULL,
        [SortOrder] int NOT NULL,
        CONSTRAINT [PK_PermissionGroups] PRIMARY KEY ([Key])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [cfg].[Positions] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(64) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Status] int NOT NULL,
        CONSTRAINT [PK_Positions] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[ProvisioningAudits] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [MerchantCode] nvarchar(64) NOT NULL,
        [AdminSubject] nvarchar(256) NOT NULL,
        [CorrelationId] nvarchar(128) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ProvisioningAudits] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [admin].[ProvisioningOperations] (
        [Id] uniqueidentifier NOT NULL,
        [OperationKey] nvarchar(200) NOT NULL,
        [CallerAdminId] uniqueidentifier NOT NULL,
        [ExpectedAuthorizationVersion] bigint NOT NULL,
        [RequestHash] nvarchar(64) NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Result] json NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ProvisioningOperations] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [txn].[PspConnections] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Psp] int NOT NULL,
        [EnabledMethods] nvarchar(256) NOT NULL,
        [SecretRefName] nvarchar(128) NOT NULL,
        [Metadata] nvarchar(max) NULL,
        [IsEnabled] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_PspConnections] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[RegistrationAudits] (
        [Id] uniqueidentifier NOT NULL,
        [Action] nvarchar(64) NOT NULL,
        [ActorSubject] nvarchar(256) NULL,
        [TargetSubject] nvarchar(256) NOT NULL,
        [Role] nvarchar(64) NULL,
        [Reason] nvarchar(1024) NULL,
        [MerchantId] uniqueidentifier NULL,
        [CorrelationId] nvarchar(128) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_RegistrationAudits] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [iam].[Roles] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(64) NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [Description] nvarchar(256) NULL,
        [Color] nvarchar(16) NULL,
        [Status] int NOT NULL,
        [Scope] int NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        CONSTRAINT [PK_Roles] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Roles_ScopeMerchant] CHECK (([Scope] = 0 AND [MerchantId] IS NULL) OR [Scope] = 1)
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [admin].[Sessions] (
        [Id] uniqueidentifier NOT NULL,
        [FamilyId] uniqueidentifier NOT NULL,
        [TokenHash] varbinary(32) NOT NULL,
        [AdminUserId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [IssuedAt] datetime2 NOT NULL,
        [IdleExpiresAt] datetime2 NOT NULL,
        [AbsoluteExpiresAt] datetime2 NOT NULL,
        [SupersededAt] datetime2 NULL,
        [SupersededBySessionId] uniqueidentifier NULL,
        [IpAddress] nvarchar(45) NULL,
        [UserAgent] nvarchar(256) NULL,
        CONSTRAINT [PK_Sessions] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[Sessions] (
        [Id] uniqueidentifier NOT NULL,
        [FamilyId] uniqueidentifier NOT NULL,
        [TokenHash] varbinary(32) NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL,
        [IssuedAt] datetime2 NOT NULL,
        [IdleExpiresAt] datetime2 NOT NULL,
        [AbsoluteExpiresAt] datetime2 NOT NULL,
        [SupersededAt] datetime2 NULL,
        [SupersededBySessionId] uniqueidentifier NULL,
        [IpAddress] nvarchar(45) NULL,
        [UserAgent] nvarchar(256) NULL,
        CONSTRAINT [PK_Sessions] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [admin].[UserAudits] (
        [Id] uniqueidentifier NOT NULL,
        [Action] nvarchar(64) NOT NULL,
        [ActorType] nvarchar(16) NOT NULL,
        [ActorId] uniqueidentifier NOT NULL,
        [TargetAdminId] uniqueidentifier NULL,
        [MerchantId] uniqueidentifier NULL,
        [TargetRoleId] uniqueidentifier NULL,
        [CorrelationId] nvarchar(128) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_UserAudits] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[UserOutbox] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Type] nvarchar(256) NOT NULL,
        [Payload] json NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        [ProcessedAt] datetime2 NULL,
        [Attempts] int NOT NULL,
        [Error] nvarchar(2048) NULL,
        [LeaseExpiresAt] datetime2 NULL,
        [LeaseOwner] nvarchar(256) NULL,
        CONSTRAINT [PK_UserOutbox] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[Users] (
        [Id] uniqueidentifier NOT NULL,
        [Subject] nvarchar(256) NOT NULL,
        [Email] nvarchar(320) NOT NULL,
        [Status] int NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [CreatedAt] datetime2 NOT NULL,
        [DisplayName] nvarchar(200) NOT NULL,
        [FirstName] nvarchar(200) NOT NULL,
        [LastName] nvarchar(200) NOT NULL,
        [IdentityType] int NULL,
        [IdentityNumber] nvarchar(64) NULL,
        [SaleCode] varchar(20) NULL,
        [LicenseNumber] nvarchar(64) NULL,
        [Phone] nvarchar(32) NULL,
        [PhotoObjectKey] nvarchar(256) NULL,
        [PhotoContentType] nvarchar(128) NULL,
        [KycPhotoObjectKey] nvarchar(256) NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[VaultRevealAudits] (
        [Id] bigint NOT NULL IDENTITY,
        [MerchantId] uniqueidentifier NOT NULL,
        [SecretName] nvarchar(128) NOT NULL,
        [Seq] bigint NOT NULL,
        [PrevHash] varbinary(32) NOT NULL,
        [Hash] varbinary(32) NOT NULL,
        [RevealedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_VaultRevealAudits] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[VaultSecrets] (
        [MerchantId] uniqueidentifier NOT NULL,
        [SecretName] nvarchar(128) NOT NULL,
        [SecretKey] nvarchar(64) NOT NULL,
        [EncryptedDek] varbinary(max) NOT NULL,
        [EncryptedSecret] varbinary(max) NOT NULL,
        [Hint] nvarchar(16) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_VaultSecrets] PRIMARY KEY ([MerchantId], [SecretName])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [shop].[CartItems] (
        [Id] uniqueidentifier NOT NULL,
        [CartId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [ProductCode] nvarchar(150) NOT NULL,
        [SaleCode] varchar(20) NOT NULL,
        [VariantCode] varchar(64) NOT NULL,
        [VariantName] nvarchar(128) NULL,
        [Quantity] int NOT NULL,
        [Metadata] json NULL,
        [UnitPriceAmount] decimal(19,4) NOT NULL,
        [UnitPriceCurrency] char(3) NOT NULL,
        CONSTRAINT [PK_CartItems] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CartItems_Carts_CartId_MerchantId] FOREIGN KEY ([CartId], [MerchantId]) REFERENCES [shop].[Carts] ([Id], [MerchantId]) ON DELETE CASCADE
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [shop].[OrderItems] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Quantity] int NOT NULL,
        [ProductCode] nvarchar(150) NOT NULL,
        [VariantCode] varchar(64) NOT NULL,
        [VariantName] nvarchar(128) NULL,
        [Metadata] json NULL,
        [DiscountAmount] decimal(19,4) NOT NULL,
        [DiscountCurrency] char(3) NOT NULL,
        [UnitPriceAmount] decimal(19,4) NOT NULL,
        [UnitPriceCurrency] char(3) NOT NULL,
        CONSTRAINT [PK_OrderItems] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OrderItems_Orders_OrderId_MerchantId] FOREIGN KEY ([OrderId], [MerchantId]) REFERENCES [shop].[Orders] ([Id], [MerchantId]) ON DELETE CASCADE
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [iam].[Permissions] (
        [Key] nvarchar(64) NOT NULL,
        [GroupKey] nvarchar(32) NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        [Status] int NOT NULL,
        [SortOrder] int NOT NULL,
        CONSTRAINT [PK_Permissions] PRIMARY KEY ([Key]),
        CONSTRAINT [FK_Permissions_PermissionGroups_GroupKey] FOREIGN KEY ([GroupKey]) REFERENCES [iam].[PermissionGroups] ([Key]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [admin].[Users] (
        [Id] uniqueidentifier NOT NULL,
        [Subject] nvarchar(256) NULL,
        [Email] nvarchar(320) NOT NULL,
        [Tier] int NOT NULL,
        [Status] int NOT NULL,
        [AuthorizationVersion] bigint NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NULL,
        [PositionId] uniqueidentifier NULL,
        [OfficeId] uniqueidentifier NULL,
        [LevelId] uniqueidentifier NULL,
        [DivisionId] uniqueidentifier NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Users_Divisions_DivisionId] FOREIGN KEY ([DivisionId]) REFERENCES [cfg].[Divisions] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Users_Levels_LevelId] FOREIGN KEY ([LevelId]) REFERENCES [cfg].[Levels] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Users_Offices_OfficeId] FOREIGN KEY ([OfficeId]) REFERENCES [cfg].[Offices] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Users_Positions_PositionId] FOREIGN KEY ([PositionId]) REFERENCES [cfg].[Positions] ([Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [admin].[RoleAssignments] (
        [Id] uniqueidentifier NOT NULL,
        [AdminUserId] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        [AssignedById] uniqueidentifier NOT NULL,
        [AssignedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_RoleAssignments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RoleAssignments_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [iam].[Roles] ([Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[RoleAssignments] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [AssignedById] uniqueidentifier NOT NULL,
        [AssignedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_RoleAssignments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RoleAssignments_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [iam].[Roles] ([Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [merch].[RegistrationAttempts] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [AttemptNo] int NOT NULL,
        [Purpose] int NOT NULL,
        [FirstName] nvarchar(200) NOT NULL,
        [LastName] nvarchar(200) NOT NULL,
        [IdentityType] int NULL,
        [IdentityNumber] nvarchar(64) NULL,
        [SaleCode] varchar(20) NULL,
        [LicenseNumber] nvarchar(64) NULL,
        [Phone] nvarchar(32) NULL,
        [Email] nvarchar(320) NOT NULL,
        [PhotoObjectKey] nvarchar(256) NULL,
        [PhotoContentType] nvarchar(128) NULL,
        [SubmittedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_RegistrationAttempts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RegistrationAttempts_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [merch].[Users] ([Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE TABLE [iam].[RolePermissions] (
        [Id] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        [PermissionKey] nvarchar(64) NOT NULL,
        CONSTRAINT [PK_RolePermissions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RolePermissions_Permissions_PermissionKey] FOREIGN KEY ([PermissionKey]) REFERENCES [iam].[Permissions] ([Key]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RolePermissions_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [iam].[Roles] ([Id]) ON DELETE CASCADE
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_AuthAudits_AdminUserId] ON [admin].[AuthAudits] ([AdminUserId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_AuthAudits_UserId] ON [merch].[AuthAudits] ([UserId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_CartItems_CartId_MerchantId] ON [shop].[CartItems] ([CartId], [MerchantId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Divisions_Code] ON [cfg].[Divisions] ([Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ExternalLogins_Provider_Subject] ON [merch].[ExternalLogins] ([Provider], [Subject]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Levels_Code] ON [cfg].[Levels] ([Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantAccess_AdminUserId_MerchantId] ON [admin].[MerchantAccess] ([AdminUserId], [MerchantId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Merchants_Code] ON [merch].[Merchants] ([Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Offices_Code] ON [cfg].[Offices] ([Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_OrderItemRevealAudits_MerchantId_RevealedAt] ON [shop].[OrderItemRevealAudits] ([MerchantId], [RevealedAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_OrderItemRevealAudits_OrderItemId] ON [shop].[OrderItemRevealAudits] ([OrderItemId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_OrderItems_OrderId_MerchantId] ON [shop].[OrderItems] ([OrderId], [MerchantId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_OrderItems_ProductCode] ON [shop].[OrderItems] ([ProductCode]) INCLUDE ([OrderId], [VariantCode]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Orders_MerchantId] ON [shop].[Orders] ([MerchantId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Orders_OrderNo] ON [shop].[Orders] ([OrderNo]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Orders_PaymentSessionId] ON [shop].[Orders] ([PaymentSessionId]) WHERE [PaymentSessionId] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Orders_SummaryToken] ON [shop].[Orders] ([SummaryToken]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_ProcessedAt_LeaseExpiresAt] ON [txn].[OutboxMessages] ([ProcessedAt], [LeaseExpiresAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_PaymentSessions_OrderId] ON [txn].[PaymentSessions] ([OrderId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_PaymentSessions_OrderId_Open] ON [txn].[PaymentSessions] ([OrderId]) WHERE [Status] IN (0, 1)');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_PaymentSessions_Psp_PspExternalChargeId] ON [txn].[PaymentSessions] ([Psp], [PspExternalChargeId]) WHERE [PspExternalChargeId] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Permissions_GroupKey] ON [iam].[Permissions] ([GroupKey]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Positions_Code] ON [cfg].[Positions] ([Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [UX_ProvisioningOperations_Key] ON [admin].[ProvisioningOperations] ([OperationKey]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PspConnections_MerchantId_Psp] ON [txn].[PspConnections] ([MerchantId], [Psp]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RegistrationAttempts_UserId_AttemptNo] ON [merch].[RegistrationAttempts] ([UserId], [AttemptNo]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_RegistrationAudits_TargetSubject] ON [merch].[RegistrationAudits] ([TargetSubject]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RoleAssignments_AdminUserId_RoleId] ON [admin].[RoleAssignments] ([AdminUserId], [RoleId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_RoleAssignments_RoleId] ON [admin].[RoleAssignments] ([RoleId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_RoleAssignments_RoleId] ON [merch].[RoleAssignments] ([RoleId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_RoleAssignments_UserId_MerchantId] ON [merch].[RoleAssignments] ([UserId], [MerchantId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RoleAssignments_UserId_RoleId] ON [merch].[RoleAssignments] ([UserId], [RoleId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_RolePermissions_PermissionKey] ON [iam].[RolePermissions] ([PermissionKey]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RolePermissions_RoleId_PermissionKey] ON [iam].[RolePermissions] ([RoleId], [PermissionKey]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Roles_MerchantId_Code] ON [iam].[Roles] ([MerchantId], [Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Sessions_AbsoluteExpiresAt] ON [admin].[Sessions] ([AbsoluteExpiresAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Sessions_AdminUserId] ON [admin].[Sessions] ([AdminUserId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Sessions_FamilyId] ON [admin].[Sessions] ([FamilyId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Sessions_TokenHash] ON [admin].[Sessions] ([TokenHash]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Sessions_AbsoluteExpiresAt] ON [merch].[Sessions] ([AbsoluteExpiresAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Sessions_FamilyId] ON [merch].[Sessions] ([FamilyId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Sessions_TokenHash] ON [merch].[Sessions] ([TokenHash]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Sessions_UserId] ON [merch].[Sessions] ([UserId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_UserOutbox_ProcessedAt_LeaseExpiresAt] ON [merch].[UserOutbox] ([ProcessedAt], [LeaseExpiresAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Users_DivisionId] ON [admin].[Users] ([DivisionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_Email] ON [admin].[Users] ([Email]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Users_LevelId] ON [admin].[Users] ([LevelId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Users_OfficeId] ON [admin].[Users] ([OfficeId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Users_PositionId] ON [admin].[Users] ([PositionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Users_Subject] ON [admin].[Users] ([Subject]) WHERE [Subject] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_Subject] ON [merch].[Users] ([Subject]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_VaultRevealAudits_MerchantId_Id] ON [merch].[VaultRevealAudits] ([MerchantId], [Id]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_VaultRevealAudits_MerchantId_Seq] ON [merch].[VaultRevealAudits] ([MerchantId], [Seq]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042818_InitialSchema'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260807042818_InitialSchema', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042828_SecurityObjects'
)
BEGIN
    CREATE SEQUENCE shop.OrderNoSeq AS bigint
        START WITH 1 INCREMENT BY 1 NO CYCLE;

    CREATE TABLE merch.RegistrationNotices (
        Id           uniqueidentifier NOT NULL CONSTRAINT PK_RegistrationNotices PRIMARY KEY,
        UserId       uniqueidentifier NOT NULL,
        Subject      nvarchar(256) NOT NULL,
        Email        nvarchar(320) NOT NULL,
        DisplayName  nvarchar(200) NOT NULL,
        HostedDomain nvarchar(256) NULL,
        OccurredAt   datetime2 NOT NULL,
        CreatedAt    datetime2 NOT NULL
    );
    CREATE UNIQUE INDEX IX_RegistrationNotices_UserId
        ON merch.RegistrationNotices (UserId);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042828_SecurityObjects'
)
BEGIN
    IF USER_ID(N'pol_app') IS NULL
        THROW 51003, N'SecurityObjects requires database user [pol_app].', 1;

    GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Carts                  TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON shop.CartItems              TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON shop.Orders                 TO pol_app;
    GRANT SELECT, INSERT                 ON shop.OrderItems             TO pol_app;
    GRANT SELECT, INSERT                 ON shop.OrderItemRevealAudits TO pol_app;
    GRANT UPDATE ON OBJECT::shop.OrderNoSeq TO pol_app;

    GRANT SELECT, INSERT, UPDATE ON txn.PaymentSessions    TO pol_app;
    GRANT SELECT, INSERT         ON txn.PspConnections    TO pol_app;
    GRANT SELECT, INSERT         ON txn.IdempotencyRecords TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON txn.OutboxMessages      TO pol_app;

    GRANT SELECT, INSERT, UPDATE         ON merch.Merchants           TO pol_app;
    GRANT SELECT, INSERT, UPDATE         ON merch.VaultSecrets        TO pol_app;
    GRANT SELECT, INSERT                 ON merch.VaultRevealAudits   TO pol_app;
    GRANT SELECT, INSERT                 ON merch.RegistrationNotices TO pol_app;
    GRANT SELECT, INSERT, UPDATE         ON merch.UserOutbox          TO pol_app;
    GRANT SELECT, INSERT, UPDATE         ON merch.Users               TO pol_app;
    GRANT SELECT, INSERT                 ON merch.ExternalLogins      TO pol_app;
    GRANT SELECT, INSERT                 ON merch.RegistrationAudits  TO pol_app;
    GRANT SELECT, INSERT                 ON merch.RegistrationAttempts TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON merch.Sessions            TO pol_app;
    GRANT SELECT, INSERT                 ON merch.AuthAudits          TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON merch.RoleAssignments     TO pol_app;
    GRANT SELECT, INSERT                 ON merch.ProvisioningAudits  TO pol_app;

    GRANT SELECT, INSERT, UPDATE         ON admin.Users                  TO pol_app;
    GRANT SELECT, INSERT                 ON admin.UserAudits             TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON admin.MerchantAccess         TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON admin.Sessions               TO pol_app;
    GRANT SELECT, INSERT                 ON admin.AuthAudits             TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON admin.RoleAssignments        TO pol_app;
    GRANT SELECT, INSERT, UPDATE         ON admin.ProvisioningOperations TO pol_app;

    GRANT SELECT, INSERT, UPDATE ON cfg.Positions TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON cfg.Offices   TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON cfg.Levels    TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON cfg.Divisions TO pol_app;

    GRANT SELECT                         ON iam.PermissionGroups TO pol_app;
    GRANT SELECT                         ON iam.Permissions      TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON iam.Roles           TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON iam.RolePermissions TO pol_app;

    GRANT SELECT, INSERT ON dbo.DataProtectionKeys TO pol_app;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042828_SecurityObjects'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260807042828_SecurityObjects', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042833_SeedData'
)
BEGIN
    INSERT INTO iam.PermissionGroups ([Key], Scope, Name, Status, SortOrder) VALUES
      ('txn',             0, N'ธุรกรรม',           0, 1),
      ('merchant',        0, N'ร้านค้า',            0, 2),
      ('user',            0, N'ผู้ใช้งาน',           0, 3),
      ('system',          0, N'ระบบ',               0, 4),
      ('merchants.users', 0, N'ผู้ใช้งานร้านค้า',     0, 5),
      ('payment',         1, N'การชำระเงิน',        0, 6),
      ('roles',           1, N'บทบาทและสิทธิ์',      0, 7);

    INSERT INTO iam.Permissions ([Key], GroupKey, Name, Status, SortOrder) VALUES
      ('txn.view',                'txn',             N'ดูรายการธุรกรรม',           0, 1),
      ('txn.refund',              'txn',             N'สั่งคืนเงิน',               0, 2),
      ('txn.export',              'txn',             N'ส่งออกข้อมูลธุรกรรม',        0, 3),
      ('merchant.view',           'merchant',        N'ดูข้อมูลร้านค้า',           0, 4),
      ('merchant.manage',         'merchant',        N'เพิ่ม/แก้ไข/ระงับร้านค้า',  0, 5),
      ('user.view',               'user',            N'ดูรายชื่อผู้ใช้งาน',        0, 6),
      ('user.manage',             'user',            N'เปิด/แก้ไข/ปิดบัญชีผู้ใช้', 0, 7),
      ('user.roles',              'user',            N'กำหนดบทบาทให้ผู้ใช้',       0, 8),
      ('audit.view',              'system',          N'ดูบันทึกกิจกรรม (audit)',   0, 9),
      ('settings.manage',         'system',          N'ตั้งค่าระบบและความปลอดภัย',  0, 10),
      ('apikey.manage',           'system',          N'จัดการ API client / secret', 0, 11),
      ('merchants.users.approve', 'merchants.users', N'อนุมัติผู้ใช้งานร้านค้า',   0, 12),
      ('merchants.users.reject',  'merchants.users', N'ปฏิเสธผู้ใช้งานร้านค้า',    0, 13),
      ('merchants.users.view',    'merchants.users', N'ดูประวัติการสมัครร้านค้า',   0, 14),
      ('payment.create',          'payment',         N'สร้างรายการชำระเงิน',       0, 15),
      ('payment.redirect',        'payment',         N'เปิดหน้าชำระเงินให้ลูกค้า',   0, 16),
      ('roles.view',              'roles',           N'ดูบทบาท',                 0, 17),
      ('roles.manage',            'roles',           N'สร้าง/แก้ไข/ลบบทบาท',       0, 18),
      ('users.roles',             'roles',           N'กำหนดบทบาทให้ผู้ใช้',       0, 19);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042833_SeedData'
)
BEGIN
    INSERT INTO iam.Roles (Id, Code, Name, Description, Color, Status, Scope, MerchantId) VALUES
      ('11111111-1111-1111-1111-111111111111',   'platform_admin',   N'ผู้ดูแลแพลตฟอร์ม', N'เข้าถึงได้ทุกส่วนของแพลตฟอร์ม รวมถึงการตั้งค่าความปลอดภัย', 'red',  0, 0, NULL),
      ('55555555-5555-5555-5555-555555555555', 'platform_auditor', N'ผู้ตรวจสอบ',       N'อ่านข้อมูลธุรกรรม/ร้านค้า/ผู้ใช้ และบันทึกกิจกรรมเท่านั้น',  'gray', 0, 0, NULL),
      ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'merchant_manager', N'ผู้จัดการร้าน',    N'เข้าถึงได้ทุกส่วนของร้าน รวมถึงการจัดการบทบาทและผู้ใช้',     'red',  0, 1, NULL),
      ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',   'merchant_staff',   N'พนักงานร้าน',      N'จัดการสินค้าและการชำระเงิน (ไม่รวมการจัดการบทบาท)',        'blue', 0, 1, NULL);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042833_SeedData'
)
BEGIN
    INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'txn.view'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'txn.refund'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'txn.export'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'merchant.view'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'merchant.manage'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'user.view'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'user.manage'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'user.roles'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'audit.view'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'settings.manage'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'apikey.manage'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'merchants.users.approve'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'merchants.users.reject'),
      (NEWID(), '11111111-1111-1111-1111-111111111111', 'merchants.users.view'),
      (NEWID(), '55555555-5555-5555-5555-555555555555', 'txn.view'),
      (NEWID(), '55555555-5555-5555-5555-555555555555', 'merchant.view'),
      (NEWID(), '55555555-5555-5555-5555-555555555555', 'user.view'),
      (NEWID(), '55555555-5555-5555-5555-555555555555', 'audit.view'),
      (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'payment.create'),
      (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'payment.redirect'),
      (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'roles.view'),
      (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'roles.manage'),
      (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'users.roles'),
      (NEWID(), 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'payment.create'),
      (NEWID(), 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'payment.redirect');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042833_SeedData'
)
BEGIN
    INSERT INTO cfg.Positions (Id, Code, Name, Status) VALUES
      ('a1000000-0000-4000-8000-000000000001', 'ceo',               N'ประธานเจ้าหน้าที่บริหาร',            0),
      ('a1000000-0000-4000-8000-000000000002', 'coo',               N'ประธานเจ้าหน้าที่ปฏิบัติการ',        0),
      ('a1000000-0000-4000-8000-000000000003', 'cfo',               N'ประธานเจ้าหน้าที่การเงิน',           0),
      ('a1000000-0000-4000-8000-000000000004', 'cto',               N'ประธานเจ้าหน้าที่เทคโนโลยีสารสนเทศ', 0),
      ('a1000000-0000-4000-8000-000000000005', 'director',          N'ผู้อำนวยการ',                       0),
      ('a1000000-0000-4000-8000-000000000006', 'deputy_director',   N'รองผู้อำนวยการ',                    0),
      ('a1000000-0000-4000-8000-000000000007', 'manager',           N'ผู้จัดการ',                         0),
      ('a1000000-0000-4000-8000-000000000008', 'assistant_manager', N'ผู้ช่วยผู้จัดการ',                   0),
      ('a1000000-0000-4000-8000-000000000009', 'supervisor',        N'หัวหน้างาน',                        0),
      ('a1000000-0000-4000-8000-00000000000a', 'senior_officer',    N'เจ้าหน้าที่อาวุโส',                 0),
      ('a1000000-0000-4000-8000-00000000000b', 'officer',           N'เจ้าหน้าที่',                       0),
      ('a1000000-0000-4000-8000-00000000000c', 'staff',             N'พนักงาน',                           0);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042833_SeedData'
)
BEGIN
    INSERT INTO cfg.Offices (Id, Code, Name, Status) VALUES
      ('b2000000-0000-4000-8000-000000000001', 'hq',        N'สำนักงานใหญ่',                     0),
      ('b2000000-0000-4000-8000-000000000002', 'north',     N'สำนักงานภาคเหนือ',                 0),
      ('b2000000-0000-4000-8000-000000000003', 'northeast', N'สำนักงานภาคตะวันออกเฉียงเหนือ',    0),
      ('b2000000-0000-4000-8000-000000000004', 'central',   N'สำนักงานภาคกลาง',                  0),
      ('b2000000-0000-4000-8000-000000000005', 'east',      N'สำนักงานภาคตะวันออก',              0),
      ('b2000000-0000-4000-8000-000000000006', 'west',      N'สำนักงานภาคตะวันตก',               0),
      ('b2000000-0000-4000-8000-000000000007', 'south',     N'สำนักงานภาคใต้',                   0),
      ('b2000000-0000-4000-8000-000000000008', 'remote',    N'ปฏิบัติงานนอกสถานที่',             0);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042833_SeedData'
)
BEGIN
    INSERT INTO cfg.Levels (Id, Code, Name, Status) VALUES
      ('c3000000-0000-4000-8000-000000000001', 'level_1',  N'ระดับ 1',  0),
      ('c3000000-0000-4000-8000-000000000002', 'level_2',  N'ระดับ 2',  0),
      ('c3000000-0000-4000-8000-000000000003', 'level_3',  N'ระดับ 3',  0),
      ('c3000000-0000-4000-8000-000000000004', 'level_4',  N'ระดับ 4',  0),
      ('c3000000-0000-4000-8000-000000000005', 'level_5',  N'ระดับ 5',  0),
      ('c3000000-0000-4000-8000-000000000006', 'level_6',  N'ระดับ 6',  0),
      ('c3000000-0000-4000-8000-000000000007', 'level_7',  N'ระดับ 7',  0),
      ('c3000000-0000-4000-8000-000000000008', 'level_8',  N'ระดับ 8',  0),
      ('c3000000-0000-4000-8000-000000000009', 'level_9',  N'ระดับ 9',  0),
      ('c3000000-0000-4000-8000-00000000000a', 'level_10', N'ระดับ 10', 0);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042833_SeedData'
)
BEGIN
    INSERT INTO cfg.Divisions (Id, Code, Name, Status) VALUES
      ('d4000000-0000-4000-8000-000000000001', 'executive',        N'สำนักผู้บริหาร',                    0),
      ('d4000000-0000-4000-8000-000000000002', 'finance',          N'ฝ่ายการเงินและบัญชี',               0),
      ('d4000000-0000-4000-8000-000000000003', 'technology',       N'ฝ่ายเทคโนโลยีสารสนเทศ',             0),
      ('d4000000-0000-4000-8000-000000000004', 'operations',       N'ฝ่ายปฏิบัติการ',                    0),
      ('d4000000-0000-4000-8000-000000000005', 'product',          N'ฝ่ายผลิตภัณฑ์',                     0),
      ('d4000000-0000-4000-8000-000000000006', 'sales_marketing',  N'ฝ่ายขายและการตลาด',                 0),
      ('d4000000-0000-4000-8000-000000000007', 'risk_compliance',  N'ฝ่ายบริหารความเสี่ยงและกำกับดูแล',  0),
      ('d4000000-0000-4000-8000-000000000008', 'legal',            N'ฝ่ายกฎหมาย',                        0),
      ('d4000000-0000-4000-8000-000000000009', 'hr',               N'ฝ่ายทรัพยากรบุคคล',                 0),
      ('d4000000-0000-4000-8000-00000000000a', 'customer_service', N'ฝ่ายบริการลูกค้า',                  0);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042833_SeedData'
)
BEGIN
    INSERT INTO merch.Merchants
        (Id, Code, Name, Note, Status, Country, Currency, EnabledChannels, CreatedAt, Metadata)
    VALUES
        ('e1000000-0000-4000-8000-000000000001', 'demo', N'ร้านค้าตัวอย่าง',
         N'Synthetic baseline data', 0, 'TH', 'THB', 'card', SYSUTCDATETIME(), '{}');

    INSERT INTO txn.PspConnections
        (Id, MerchantId, Psp, EnabledMethods, SecretRefName, Metadata, IsEnabled, CreatedAt)
    VALUES
        ('e8000000-0000-4000-8000-000000000001',
         'e1000000-0000-4000-8000-000000000001', 0, 'card',
         'demo-disabled', NULL, 0, SYSUTCDATETIME());
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260807042833_SeedData'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260807042833_SeedData', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260808161508_OneBasedPersistedEnumStorage'
)
BEGIN
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
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260808161508_OneBasedPersistedEnumStorage'
)
BEGIN
    ALTER TABLE [iam].[Roles] DROP CONSTRAINT [CK_Roles_ScopeMerchant];
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260808161508_OneBasedPersistedEnumStorage'
)
BEGIN
    DROP INDEX [IX_PaymentSessions_OrderId_Open] ON [txn].[PaymentSessions];
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260808161508_OneBasedPersistedEnumStorage'
)
BEGIN
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
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260808161508_OneBasedPersistedEnumStorage'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[merch].[Users]') AND [c].[name] = N'IdentityType');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [merch].[Users] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [merch].[Users] ALTER COLUMN [IdentityType] int NOT NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260808161508_OneBasedPersistedEnumStorage'
)
BEGIN
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[merch].[RegistrationAttempts]') AND [c].[name] = N'IdentityType');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [merch].[RegistrationAttempts] DROP CONSTRAINT ' + @var1 + ';');
    ALTER TABLE [merch].[RegistrationAttempts] ALTER COLUMN [IdentityType] int NOT NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260808161508_OneBasedPersistedEnumStorage'
)
BEGIN
    EXEC(N'ALTER TABLE [iam].[Roles] ADD CONSTRAINT [CK_Roles_ScopeMerchant] CHECK (([Scope] = 1 AND [MerchantId] IS NULL) OR [Scope] = 2)');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260808161508_OneBasedPersistedEnumStorage'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_PaymentSessions_OrderId_Open] ON [txn].[PaymentSessions] ([OrderId]) WHERE [Status] IN (1, 2)');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260808161508_OneBasedPersistedEnumStorage'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260808161508_OneBasedPersistedEnumStorage', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260809183210_MerchantRealApiIdentity'
)
BEGIN
    CREATE TABLE [merch].[MerchantUserInvitations] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Email] nvarchar(320) NOT NULL,
        [NormalizedEmail] nvarchar(320) NOT NULL,
        [TokenHash] varchar(64) NOT NULL,
        [ExpiresAt] datetime2 NOT NULL,
        [AcceptedAt] datetime2 NULL,
        [AcceptedByUserId] uniqueidentifier NULL,
        [RevokedAt] datetime2 NULL,
        [CreatedByUserId] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_MerchantUserInvitations] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260809183210_MerchantRealApiIdentity'
)
BEGIN
    CREATE TABLE [merch].[MerchantUserManagementAudits] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [ActorUserId] uniqueidentifier NULL,
        [TargetUserId] uniqueidentifier NULL,
        [InvitationId] uniqueidentifier NULL,
        [Action] varchar(32) NOT NULL,
        [CorrelationId] nvarchar(128) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_MerchantUserManagementAudits] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_MerchantUserManagementAudits_Target] CHECK ([TargetUserId] IS NOT NULL OR [InvitationId] IS NOT NULL)
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260809183210_MerchantRealApiIdentity'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_MerchantUserInvitations_MerchantId_NormalizedEmail] ON [merch].[MerchantUserInvitations] ([MerchantId], [NormalizedEmail]) WHERE [AcceptedAt] IS NULL AND [RevokedAt] IS NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260809183210_MerchantRealApiIdentity'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantUserInvitations_TokenHash] ON [merch].[MerchantUserInvitations] ([TokenHash]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260809183210_MerchantRealApiIdentity'
)
BEGIN
    CREATE INDEX [IX_MerchantUserManagementAudits_MerchantId_OccurredAt] ON [merch].[MerchantUserManagementAudits] ([MerchantId], [OccurredAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260809183210_MerchantRealApiIdentity'
)
BEGIN
    GRANT SELECT, INSERT, UPDATE ON merch.MerchantUserInvitations TO pol_app;
    GRANT SELECT, INSERT ON merch.MerchantUserManagementAudits TO pol_app;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260809183210_MerchantRealApiIdentity'
)
BEGIN
    INSERT INTO iam.Permissions ([Key], GroupKey, Name, Status, SortOrder) VALUES
      ('payment.view', 'payment', N'ดูรายการชำระเงิน', 1, 20),
      ('users.view',   'roles',   N'ดูผู้ใช้งานร้านค้า', 1, 21),
      ('users.manage', 'roles',   N'จัดการผู้ใช้งานร้านค้า', 1, 22);

    INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
      (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'payment.view'),
      (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'users.view'),
      (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'users.manage'),
      (NEWID(), 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'payment.view');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260809183210_MerchantRealApiIdentity'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260809183210_MerchantRealApiIdentity', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810041211_AdminConsolePermissionKeys'
)
BEGIN
    INSERT INTO iam.Permissions ([Key], GroupKey, Name, Status, SortOrder) VALUES
      ('txn.manage',             'txn',             N'จัดการธุรกรรม',             1, 23),
      ('merchants.users.manage', 'merchants.users', N'จัดการผู้ใช้งานร้านค้า',    1, 24),
      ('merchants.roles.view',   'merchants.users', N'ดูบทบาทผู้ใช้งานร้านค้า',    1, 25),
      ('merchants.roles.manage', 'merchants.users', N'จัดการบทบาทผู้ใช้งานร้านค้า', 1, 26);

    INSERT INTO iam.RolePermissions (Id, RoleId, PermissionKey) VALUES
      ('f9000000-0000-4000-8000-000000000001', '11111111-1111-1111-1111-111111111111', 'txn.manage'),
      ('f9000000-0000-4000-8000-000000000002', '11111111-1111-1111-1111-111111111111', 'merchants.users.manage'),
      ('f9000000-0000-4000-8000-000000000003', '11111111-1111-1111-1111-111111111111', 'merchants.roles.view'),
      ('f9000000-0000-4000-8000-000000000004', '11111111-1111-1111-1111-111111111111', 'merchants.roles.manage');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810041211_AdminConsolePermissionKeys'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260810041211_AdminConsolePermissionKeys', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE TABLE [admin].[ApprovalRequests] (
        [Id] uniqueidentifier NOT NULL,
        [ScopeKind] int NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [Action] varchar(120) NOT NULL,
        [RequiredPermission] varchar(120) NOT NULL,
        [MakerId] uniqueidentifier NOT NULL,
        [TargetType] varchar(120) NOT NULL,
        [TargetId] nvarchar(200) NOT NULL,
        [TargetVersion] varchar(200) NOT NULL,
        [Status] int NOT NULL,
        [CheckerId] uniqueidentifier NULL,
        [DecisionReason] nvarchar(1000) NULL,
        [DecidedAt] datetime2 NULL,
        [ExecutionOutcome] nvarchar(1000) NULL,
        [ExecutedAt] datetime2 NULL,
        [CorrelationId] varchar(128) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_ApprovalRequests] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ApprovalRequests_Scope] CHECK (([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL))
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE TABLE [admin].[AuditHeads] (
        [ScopeKey] varchar(80) NOT NULL,
        [ScopeKind] int NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [LastSequence] bigint NOT NULL,
        [LastHash] binary(32) NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AuditHeads] PRIMARY KEY ([ScopeKey]),
        CONSTRAINT [CK_AuditHeads_Scope] CHECK (([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL))
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE TABLE [admin].[GovernanceOutboxMessages] (
        [Id] uniqueidentifier NOT NULL,
        [ScopeKind] int NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [Type] varchar(200) NOT NULL,
        [SchemaVersion] varchar(16) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        [ProcessedAt] datetime2 NULL,
        [Attempts] int NOT NULL,
        [Error] nvarchar(1000) NULL,
        [LeaseExpiresAt] datetime2 NULL,
        [LeaseOwner] nvarchar(200) NULL,
        CONSTRAINT [PK_GovernanceOutboxMessages] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_GovernanceOutboxMessages_Scope] CHECK (([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL))
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE TABLE [admin].[OperationRecords] (
        [Id] uniqueidentifier NOT NULL,
        [ActorId] uniqueidentifier NOT NULL,
        [Operation] varchar(120) NOT NULL,
        [IdempotencyKey] varchar(200) NOT NULL,
        [RequestHash] varchar(64) NOT NULL,
        [ScopeKind] int NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [Status] int NOT NULL,
        [ResponseStatus] int NULL,
        [ResponseBody] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NOT NULL,
        [CompletedAt] datetime2 NULL,
        CONSTRAINT [PK_OperationRecords] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_OperationRecords_Scope] CHECK (([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL))
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE TABLE [admin].[ApprovalEvents] (
        [Id] uniqueidentifier NOT NULL,
        [SourceEventId] uniqueidentifier NOT NULL,
        [ApprovalId] uniqueidentifier NOT NULL,
        [ScopeKind] int NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [Kind] varchar(40) NOT NULL,
        [ActorId] uniqueidentifier NULL,
        [Detail] nvarchar(1000) NULL,
        [CorrelationId] varchar(128) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ApprovalEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ApprovalEvents_Scope] CHECK (([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)),
        CONSTRAINT [FK_ApprovalEvents_ApprovalRequests_ApprovalId] FOREIGN KEY ([ApprovalId]) REFERENCES [admin].[ApprovalRequests] ([Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE TABLE [admin].[AuditRecords] (
        [Id] uniqueidentifier NOT NULL,
        [ScopeKey] varchar(80) NOT NULL,
        [ScopeKind] int NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [Sequence] bigint NOT NULL,
        [ActorId] uniqueidentifier NOT NULL,
        [Action] varchar(120) NOT NULL,
        [ResourceType] varchar(120) NOT NULL,
        [ResourceId] nvarchar(200) NOT NULL,
        [Result] varchar(80) NOT NULL,
        [Changes] nvarchar(max) NOT NULL,
        [ApprovalId] uniqueidentifier NULL,
        [ResourceVersion] varchar(200) NULL,
        [CorrelationId] varchar(128) NOT NULL,
        [OccurredAt] datetime2 NOT NULL,
        [PreviousHash] binary(32) NOT NULL,
        [Hash] binary(32) NOT NULL,
        CONSTRAINT [PK_AuditRecords] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AuditRecords_Scope] CHECK (([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)),
        CONSTRAINT [FK_AuditRecords_AuditHeads_ScopeKey] FOREIGN KEY ([ScopeKey]) REFERENCES [admin].[AuditHeads] ([ScopeKey]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE INDEX [IX_ApprovalEvents_ApprovalId_OccurredAt] ON [admin].[ApprovalEvents] ([ApprovalId], [OccurredAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ApprovalEvents_SourceEventId] ON [admin].[ApprovalEvents] ([SourceEventId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE INDEX [IX_ApprovalRequests_MerchantId_CreatedAt] ON [admin].[ApprovalRequests] ([MerchantId], [CreatedAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE INDEX [IX_ApprovalRequests_Status_CreatedAt] ON [admin].[ApprovalRequests] ([Status], [CreatedAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_AuditHeads_ScopeKind_MerchantId] ON [admin].[AuditHeads] ([ScopeKind], [MerchantId]) WHERE [MerchantId] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE INDEX [IX_AuditRecords_Action_OccurredAt] ON [admin].[AuditRecords] ([Action], [OccurredAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE INDEX [IX_AuditRecords_ActorId_OccurredAt] ON [admin].[AuditRecords] ([ActorId], [OccurredAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AuditRecords_ScopeKey_PreviousHash] ON [admin].[AuditRecords] ([ScopeKey], [PreviousHash]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AuditRecords_ScopeKey_Sequence] ON [admin].[AuditRecords] ([ScopeKey], [Sequence]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE INDEX [IX_GovernanceOutboxMessages_ProcessedAt_LeaseExpiresAt] ON [admin].[GovernanceOutboxMessages] ([ProcessedAt], [LeaseExpiresAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_OperationRecords_ActorId_Operation_IdempotencyKey] ON [admin].[OperationRecords] ([ActorId], [Operation], [IdempotencyKey]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    CREATE INDEX [IX_OperationRecords_ExpiresAt] ON [admin].[OperationRecords] ([ExpiresAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    IF USER_ID(N'pol_app') IS NULL
        THROW 51003, N'GovernanceFoundation requires database user [pol_app].', 1;

    GRANT SELECT, INSERT, UPDATE         ON admin.ApprovalRequests         TO pol_app;
    GRANT SELECT, INSERT                 ON admin.ApprovalEvents           TO pol_app;
    GRANT SELECT, INSERT, UPDATE         ON admin.GovernanceOutboxMessages TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON admin.OperationRecords         TO pol_app;
    GRANT SELECT, INSERT, UPDATE         ON admin.AuditHeads               TO pol_app;
    GRANT SELECT, INSERT                 ON admin.AuditRecords             TO pol_app;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055607_GovernanceFoundation'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260810055607_GovernanceFoundation', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055818_GovernancePlatformHeadUniqueness'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_AuditHeads_PlatformScope] ON [admin].[AuditHeads] ([ScopeKind]) WHERE [MerchantId] IS NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810055818_GovernancePlatformHeadUniqueness'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260810055818_GovernancePlatformHeadUniqueness', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810074055_AdminConsoleResourceVersions'
)
BEGIN
    ALTER TABLE [admin].[Users] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810074055_AdminConsoleResourceVersions'
)
BEGIN
    ALTER TABLE [iam].[Roles] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810074055_AdminConsoleResourceVersions'
)
BEGIN
    ALTER TABLE [cfg].[Positions] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810074055_AdminConsoleResourceVersions'
)
BEGIN
    ALTER TABLE [cfg].[Offices] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810074055_AdminConsoleResourceVersions'
)
BEGIN
    ALTER TABLE [cfg].[Levels] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810074055_AdminConsoleResourceVersions'
)
BEGIN
    ALTER TABLE [cfg].[Divisions] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810074055_AdminConsoleResourceVersions'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260810074055_AdminConsoleResourceVersions', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD [ActiveSecretVersionId] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD [Health] int NOT NULL DEFAULT 1;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD [LastTestResult] nvarchar(500) NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD [LastTestedAt] datetime2 NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD [PendingApprovalId] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD [PendingSecretVersionId] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [merch].[Merchants] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD CONSTRAINT [AK_PspConnections_MerchantId_Id] UNIQUE ([MerchantId], [Id]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE TABLE [txn].[AdminOperationRecords] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [ActorId] uniqueidentifier NOT NULL,
        [Operation] nvarchar(120) NOT NULL,
        [IdempotencyKey] nvarchar(200) NOT NULL,
        [IntentHash] nvarchar(64) NOT NULL,
        [State] int NOT NULL,
        [HttpStatus] int NULL,
        [Result] nvarchar(max) NULL,
        [ResourceId] nvarchar(200) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AdminOperationRecords] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE TABLE [merch].[Originators] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Code] nvarchar(64) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Type] int NOT NULL,
        [SaleCode] nvarchar(100) NULL,
        [ApiClientId] uniqueidentifier NULL,
        [Status] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_Originators] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_Originators_MerchantId_Id] UNIQUE ([MerchantId], [Id]),
        CONSTRAINT [FK_Originators_Merchants_MerchantId] FOREIGN KEY ([MerchantId]) REFERENCES [merch].[Merchants] ([Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE TABLE [txn].[RoutingRulesets] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Status] int NOT NULL,
        [ApprovalId] uniqueidentifier NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_RoutingRulesets] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_RoutingRulesets_MerchantId_Id] UNIQUE ([MerchantId], [Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE TABLE [merch].[VaultSecretVersions] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [SecretName] nvarchar(128) NOT NULL,
        [Version] int NOT NULL,
        [SecretKey] nvarchar(64) NOT NULL,
        [EncryptedDek] varbinary(max) NOT NULL,
        [EncryptedSecret] varbinary(max) NOT NULL,
        [Hint] nvarchar(512) NOT NULL,
        [State] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NULL,
        [ActivatedAt] datetime2 NULL,
        [RetiredAt] datetime2 NULL,
        CONSTRAINT [PK_VaultSecretVersions] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_VaultSecretVersions_MerchantId_Id] UNIQUE ([MerchantId], [Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE TABLE [txn].[RoutingRules] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [RulesetId] uniqueidentifier NOT NULL,
        [Priority] int NOT NULL,
        [Method] nvarchar(30) NOT NULL,
        [OriginatorId] uniqueidentifier NULL,
        [MinAmount] decimal(18,2) NULL,
        [MaxAmount] decimal(18,2) NULL,
        [TargetConnectionId] uniqueidentifier NOT NULL,
        [FallbackConnectionId] uniqueidentifier NULL,
        [Enabled] bit NOT NULL,
        CONSTRAINT [PK_RoutingRules] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RoutingRules_Originators_MerchantId_OriginatorId] FOREIGN KEY ([MerchantId], [OriginatorId]) REFERENCES [merch].[Originators] ([MerchantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RoutingRules_PspConnections_MerchantId_FallbackConnectionId] FOREIGN KEY ([MerchantId], [FallbackConnectionId]) REFERENCES [txn].[PspConnections] ([MerchantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RoutingRules_PspConnections_MerchantId_TargetConnectionId] FOREIGN KEY ([MerchantId], [TargetConnectionId]) REFERENCES [txn].[PspConnections] ([MerchantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RoutingRules_RoutingRulesets_MerchantId_RulesetId] FOREIGN KEY ([MerchantId], [RulesetId]) REFERENCES [txn].[RoutingRulesets] ([MerchantId], [Id]) ON DELETE CASCADE
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE INDEX [IX_PspConnections_MerchantId_ActiveSecretVersionId] ON [txn].[PspConnections] ([MerchantId], [ActiveSecretVersionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE INDEX [IX_PspConnections_MerchantId_PendingSecretVersionId] ON [txn].[PspConnections] ([MerchantId], [PendingSecretVersionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE INDEX [IX_AdminOperationRecords_ExpiresAt] ON [txn].[AdminOperationRecords] ([ExpiresAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE UNIQUE INDEX [IX_AdminOperationRecords_MerchantId_ActorId_Operation_IdempotencyKey] ON [txn].[AdminOperationRecords] ([MerchantId], [ActorId], [Operation], [IdempotencyKey]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Originators_MerchantId_Code] ON [merch].[Originators] ([MerchantId], [Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE INDEX [IX_RoutingRules_MerchantId_FallbackConnectionId] ON [txn].[RoutingRules] ([MerchantId], [FallbackConnectionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE INDEX [IX_RoutingRules_MerchantId_OriginatorId] ON [txn].[RoutingRules] ([MerchantId], [OriginatorId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RoutingRules_MerchantId_RulesetId_Priority] ON [txn].[RoutingRules] ([MerchantId], [RulesetId], [Priority]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE INDEX [IX_RoutingRules_MerchantId_TargetConnectionId] ON [txn].[RoutingRules] ([MerchantId], [TargetConnectionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_RoutingRulesets_MerchantId_Status] ON [txn].[RoutingRulesets] ([MerchantId], [Status]) WHERE [Status] = 3');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_VaultSecretVersions_MerchantId_SecretName_State] ON [merch].[VaultSecretVersions] ([MerchantId], [SecretName], [State]) WHERE [State] = 2');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    CREATE UNIQUE INDEX [IX_VaultSecretVersions_MerchantId_SecretName_Version] ON [merch].[VaultSecretVersions] ([MerchantId], [SecretName], [Version]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD CONSTRAINT [FK_PspConnections_VaultSecretVersions_MerchantId_ActiveSecretVersionId] FOREIGN KEY ([MerchantId], [ActiveSecretVersionId]) REFERENCES [merch].[VaultSecretVersions] ([MerchantId], [Id]) ON DELETE NO ACTION;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD CONSTRAINT [FK_PspConnections_VaultSecretVersions_MerchantId_PendingSecretVersionId] FOREIGN KEY ([MerchantId], [PendingSecretVersionId]) REFERENCES [merch].[VaultSecretVersions] ([MerchantId], [Id]) ON DELETE NO ACTION;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    ALTER TABLE txn.PspConnections ADD CONSTRAINT CK_PspConnections_AdminControl
        CHECK ([Health] IN (1,2,3) AND [Version] >= 1
           AND (([PendingSecretVersionId] IS NULL AND [PendingApprovalId] IS NULL)
             OR ([PendingSecretVersionId] IS NOT NULL AND [PendingApprovalId] IS NOT NULL))
           AND ([ActiveSecretVersionId] IS NULL OR [PendingSecretVersionId] IS NULL
             OR [ActiveSecretVersionId] <> [PendingSecretVersionId]));
    ALTER TABLE merch.Merchants ADD CONSTRAINT CK_Merchants_Version CHECK ([Version] >= 1);
    ALTER TABLE txn.AdminOperationRecords ADD CONSTRAINT CK_AdminOperationRecords_State
        CHECK ([MerchantId] <> '00000000-0000-0000-0000-000000000000'
           AND [ActorId] <> '00000000-0000-0000-0000-000000000000'
           AND [State] IN (1,2,3));
    ALTER TABLE merch.Originators ADD CONSTRAINT CK_Originators_Control
        CHECK ([MerchantId] <> '00000000-0000-0000-0000-000000000000'
           AND [Type] IN (1,2,3,4,5) AND [Status] IN (1,2) AND [Version] >= 1);
    ALTER TABLE txn.RoutingRulesets ADD CONSTRAINT CK_RoutingRulesets_Control
        CHECK ([MerchantId] <> '00000000-0000-0000-0000-000000000000'
           AND [Status] IN (1,2,3,4) AND [Version] >= 1);
    ALTER TABLE txn.RoutingRules ADD CONSTRAINT CK_RoutingRules_Control
        CHECK ([MerchantId] <> '00000000-0000-0000-0000-000000000000'
           AND [Priority] > 0 AND [TargetConnectionId] <> '00000000-0000-0000-0000-000000000000'
           AND ([FallbackConnectionId] IS NULL OR [FallbackConnectionId] <> [TargetConnectionId])
           AND ([MinAmount] IS NULL OR [MinAmount] >= 0)
           AND ([MaxAmount] IS NULL OR [MaxAmount] >= 0)
           AND ([MinAmount] IS NULL OR [MaxAmount] IS NULL OR [MinAmount] <= [MaxAmount]));
    ALTER TABLE merch.VaultSecretVersions ADD CONSTRAINT CK_VaultSecretVersions_Control
        CHECK ([MerchantId] <> '00000000-0000-0000-0000-000000000000'
           AND [Version] >= 1 AND [State] IN (1,2,3,4));

    GRANT UPDATE ON merch.Merchants TO pol_app;
    GRANT UPDATE ON txn.PspConnections TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON merch.Originators TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON txn.RoutingRulesets TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON txn.RoutingRules TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON merch.VaultSecretVersions TO pol_app;
    GRANT SELECT, INSERT, DELETE ON txn.AdminOperationRecords TO pol_app;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810112718_AdminTenantPspRoutingControlPlane'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260810112718_AdminTenantPspRoutingControlPlane', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810133139_AdminMerchantIdentityControl'
)
BEGIN
    ALTER TABLE [merch].[Users] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810133139_AdminMerchantIdentityControl'
)
BEGIN
    ALTER TABLE [merch].[MerchantUserInvitations] ADD [CreatedByAudience] nvarchar(32) NOT NULL DEFAULT N'MerchantUser';
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810133139_AdminMerchantIdentityControl'
)
BEGIN
    ALTER TABLE [merch].[MerchantUserInvitations] ADD [IntendedRoleCodesJson] nvarchar(2000) NOT NULL DEFAULT N'[]';
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810133139_AdminMerchantIdentityControl'
)
BEGIN
    CREATE TABLE [merch].[AdminUserOperationRecords] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [ActorId] uniqueidentifier NOT NULL,
        [Operation] nvarchar(120) NOT NULL,
        [IdempotencyKey] nvarchar(200) NOT NULL,
        [IntentHash] varchar(64) NOT NULL,
        [Result] nvarchar(max) NOT NULL,
        [HttpStatus] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AdminUserOperationRecords] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810133139_AdminMerchantIdentityControl'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_AdminUserOperationRecords_ActorId_Operation_IdempotencyKey] ON [merch].[AdminUserOperationRecords] ([ActorId], [Operation], [IdempotencyKey]) WHERE [MerchantId] IS NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810133139_AdminMerchantIdentityControl'
)
BEGIN
    CREATE INDEX [IX_AdminUserOperationRecords_ExpiresAt] ON [merch].[AdminUserOperationRecords] ([ExpiresAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810133139_AdminMerchantIdentityControl'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_AdminUserOperationRecords_MerchantId_ActorId_Operation_IdempotencyKey] ON [merch].[AdminUserOperationRecords] ([MerchantId], [ActorId], [Operation], [IdempotencyKey]) WHERE [MerchantId] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810133139_AdminMerchantIdentityControl'
)
BEGIN
    ALTER TABLE merch.Users ADD CONSTRAINT CK_Users_Version CHECK ([Version] >= 1);
    ALTER TABLE merch.MerchantUserInvitations ADD CONSTRAINT CK_MerchantUserInvitations_CreatedByAudience
        CHECK ([CreatedByAudience] IN ('MerchantUser', 'Admin'));
    ALTER TABLE merch.AdminUserOperationRecords ADD CONSTRAINT CK_AdminUserOperationRecords_Control
        CHECK ([ActorId] <> '00000000-0000-0000-0000-000000000000'
           AND [HttpStatus] BETWEEN 200 AND 299 AND [ExpiresAt] > [CreatedAt]);
    GRANT SELECT, INSERT ON merch.AdminUserOperationRecords TO pol_app;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810133139_AdminMerchantIdentityControl'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260810133139_AdminMerchantIdentityControl', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810150130_AdminCommerceLifecycle'
)
BEGIN
    ALTER TABLE [txn].[PaymentSessions] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810150130_AdminCommerceLifecycle'
)
BEGIN
    ALTER TABLE [shop].[Orders] ADD [OriginatorId] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810150130_AdminCommerceLifecycle'
)
BEGIN
    ALTER TABLE [shop].[Orders] ADD [UpdatedAt] datetime2 NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810150130_AdminCommerceLifecycle'
)
BEGIN
    UPDATE shop.Orders
    SET UpdatedAt = CreatedAt
    WHERE UpdatedAt IS NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810150130_AdminCommerceLifecycle'
)
BEGIN
    DECLARE @var2 nvarchar(max);
    SELECT @var2 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[shop].[Orders]') AND [c].[name] = N'UpdatedAt');
    IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [shop].[Orders] DROP CONSTRAINT ' + @var2 + ';');
    ALTER TABLE [shop].[Orders] ALTER COLUMN [UpdatedAt] datetime2 NOT NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810150130_AdminCommerceLifecycle'
)
BEGIN
    ALTER TABLE [shop].[Orders] ADD [Version] bigint NOT NULL DEFAULT CAST(1 AS bigint);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810150130_AdminCommerceLifecycle'
)
BEGIN
    ALTER TABLE [shop].[Carts] ADD [OriginatorId] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810150130_AdminCommerceLifecycle'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260810150130_AdminCommerceLifecycle', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810153008_AdminCommerceUpdatedAtDefault'
)
BEGIN
    DECLARE @var3 nvarchar(max);
    SELECT @var3 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[shop].[Orders]') AND [c].[name] = N'UpdatedAt');
    IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [shop].[Orders] DROP CONSTRAINT ' + @var3 + ';');
    ALTER TABLE [shop].[Orders] ADD DEFAULT (SYSUTCDATETIME()) FOR [UpdatedAt];
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810153008_AdminCommerceUpdatedAtDefault'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260810153008_AdminCommerceUpdatedAtDefault', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810162000_AdminCommerceOperationUpdateGrant'
)
BEGIN
    GRANT UPDATE ON txn.AdminOperationRecords TO pol_app;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810162000_AdminCommerceOperationUpdateGrant'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260810162000_AdminCommerceOperationUpdateGrant', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE TABLE [iam].[ApiClients] (
        [Id] uniqueidentifier NOT NULL,
        [PublicClientId] nvarchar(80) NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [OriginatorId] uniqueidentifier NULL,
        [ScopesCsv] nvarchar(1000) NOT NULL,
        [IpPolicy] nvarchar(2000) NULL,
        [SecretHash] varbinary(32) NOT NULL,
        [SecretHint] nvarchar(32) NOT NULL,
        [Status] int NOT NULL,
        [PendingRotationApprovalId] uniqueidentifier NULL,
        [PendingRotationTicketId] uniqueidentifier NULL,
        [LastUsedAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_ApiClients] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE TABLE [admin].[DeliverySecretVersions] (
        [Id] uniqueidentifier NOT NULL,
        [OwnerId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [OwnerType] nvarchar(64) NOT NULL,
        [ProtectedSecret] nvarchar(max) NOT NULL,
        [State] int NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ActivatedAt] datetime2 NULL,
        [RetiredAt] datetime2 NULL,
        CONSTRAINT [PK_DeliverySecretVersions] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE TABLE [txn].[InboundWebhookEvents] (
        [Id] uniqueidentifier NOT NULL,
        [PspConnectionId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [PaymentSessionId] uniqueidentifier NULL,
        [OrderId] uniqueidentifier NULL,
        [PspCode] varchar(32) NOT NULL,
        [ExternalEventId] nvarchar(256) NOT NULL,
        [PayloadFingerprint] varchar(64) NOT NULL,
        [SignatureValid] bit NOT NULL,
        [Status] int NOT NULL,
        [FailureCode] varchar(64) NULL,
        [ReceivedAt] datetime2 NOT NULL,
        [ProcessedAt] datetime2 NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_InboundWebhookEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_InboundWebhookEvents_PspConnections_MerchantId_PspConnectionId] FOREIGN KEY ([MerchantId], [PspConnectionId]) REFERENCES [txn].[PspConnections] ([MerchantId], [Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE TABLE [admin].[NotificationDeliveries] (
        [Id] uniqueidentifier NOT NULL,
        [RuleId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [SourceEventId] uniqueidentifier NOT NULL,
        [EventType] nvarchar(160) NOT NULL,
        [Channel] nvarchar(32) NOT NULL,
        [DestinationMasked] nvarchar(256) NOT NULL,
        [Status] int NOT NULL,
        [FailureCode] nvarchar(120) NULL,
        [SentAt] datetime2 NOT NULL,
        CONSTRAINT [PK_NotificationDeliveries] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE TABLE [admin].[NotificationRules] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [EventType] nvarchar(160) NOT NULL,
        [Channel] nvarchar(32) NOT NULL,
        [Destination] nvarchar(2048) NOT NULL,
        [Threshold] nvarchar(200) NULL,
        [Enabled] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_NotificationRules] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE TABLE [iam].[OneTimeSecretTickets] (
        [Id] uniqueidentifier NOT NULL,
        [ApiClientId] uniqueidentifier NOT NULL,
        [ApprovalId] uniqueidentifier NULL,
        [TicketHash] varbinary(32) NOT NULL,
        [ProtectedSecret] nvarchar(max) NULL,
        [Status] int NOT NULL,
        [ExpiresAt] datetime2 NOT NULL,
        [ConsumedAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_OneTimeSecretTickets] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE TABLE [admin].[WebhookDeliveries] (
        [Id] uniqueidentifier NOT NULL,
        [EndpointId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [SourceEventId] uniqueidentifier NOT NULL,
        [OriginalDeliveryId] uniqueidentifier NULL,
        [ReplayKey] nvarchar(200) NULL,
        [EventType] nvarchar(160) NOT NULL,
        [TransactionId] nvarchar(200) NULL,
        [Payload] nvarchar(max) NOT NULL,
        [Status] int NOT NULL,
        [AttemptCount] int NOT NULL,
        [NextAttemptAt] datetime2 NOT NULL,
        [LastAttemptAt] datetime2 NULL,
        [LeaseExpiresAt] datetime2 NULL,
        [LeaseOwner] nvarchar(200) NULL,
        [LatencyMs] int NULL,
        [FailureCode] nvarchar(120) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CompletedAt] datetime2 NULL,
        CONSTRAINT [PK_WebhookDeliveries] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE TABLE [admin].[WebhookEndpoints] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        [Url] nvarchar(2048) NOT NULL,
        [EventsCsv] nvarchar(2000) NOT NULL,
        [Enabled] bit NOT NULL,
        [ActiveSecretVersionId] uniqueidentifier NOT NULL,
        [SecretHint] nvarchar(32) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_WebhookEndpoints] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_ApiClients_MerchantId_Status] ON [iam].[ApiClients] ([MerchantId], [Status]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_ApiClients_PendingRotationApprovalId] ON [iam].[ApiClients] ([PendingRotationApprovalId]) WHERE [PendingRotationApprovalId] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ApiClients_PublicClientId] ON [iam].[ApiClients] ([PublicClientId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_DeliverySecretVersions_OwnerType_OwnerId_State] ON [admin].[DeliverySecretVersions] ([OwnerType], [OwnerId], [State]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_InboundWebhookEvents_MerchantId_PspConnectionId] ON [txn].[InboundWebhookEvents] ([MerchantId], [PspConnectionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_InboundWebhookEvents_MerchantId_ReceivedAt] ON [txn].[InboundWebhookEvents] ([MerchantId], [ReceivedAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE UNIQUE INDEX [IX_InboundWebhookEvents_PspConnectionId_ExternalEventId] ON [txn].[InboundWebhookEvents] ([PspConnectionId], [ExternalEventId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_InboundWebhookEvents_Status_ReceivedAt] ON [txn].[InboundWebhookEvents] ([Status], [ReceivedAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_MerchantId_SentAt] ON [admin].[NotificationDeliveries] ([MerchantId], [SentAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE UNIQUE INDEX [IX_NotificationDeliveries_RuleId_SourceEventId] ON [admin].[NotificationDeliveries] ([RuleId], [SourceEventId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_NotificationRules_MerchantId_Enabled] ON [admin].[NotificationRules] ([MerchantId], [Enabled]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_OneTimeSecretTickets_ApprovalId] ON [iam].[OneTimeSecretTickets] ([ApprovalId]) WHERE [ApprovalId] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_OneTimeSecretTickets_ExpiresAt] ON [iam].[OneTimeSecretTickets] ([ExpiresAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE UNIQUE INDEX [IX_OneTimeSecretTickets_TicketHash] ON [iam].[OneTimeSecretTickets] ([TicketHash]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_WebhookDeliveries_EndpointId_SourceEventId] ON [admin].[WebhookDeliveries] ([EndpointId], [SourceEventId]) WHERE [OriginalDeliveryId] IS NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_WebhookDeliveries_MerchantId_Status_CreatedAt] ON [admin].[WebhookDeliveries] ([MerchantId], [Status], [CreatedAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_WebhookDeliveries_OriginalDeliveryId_ReplayKey] ON [admin].[WebhookDeliveries] ([OriginalDeliveryId], [ReplayKey]) WHERE [OriginalDeliveryId] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_WebhookDeliveries_Status_NextAttemptAt_LeaseExpiresAt] ON [admin].[WebhookDeliveries] ([Status], [NextAttemptAt], [LeaseExpiresAt]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    CREATE INDEX [IX_WebhookEndpoints_MerchantId_Enabled] ON [admin].[WebhookEndpoints] ([MerchantId], [Enabled]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260810184403_AdminDeliveryControlAndInboundWebhook'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260810184403_AdminDeliveryControlAndInboundWebhook', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811024015_AdminDeliveryRuntimeGrants'
)
BEGIN
    GRANT SELECT, INSERT, UPDATE         ON iam.ApiClients              TO pol_app;
    GRANT SELECT, INSERT, UPDATE         ON iam.OneTimeSecretTickets    TO pol_app;
    GRANT SELECT, INSERT, UPDATE         ON admin.DeliverySecretVersions TO pol_app;
    GRANT SELECT, INSERT, UPDATE         ON txn.InboundWebhookEvents    TO pol_app;
    GRANT SELECT, INSERT                 ON admin.NotificationDeliveries TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON admin.NotificationRules     TO pol_app;
    GRANT SELECT, INSERT, UPDATE         ON admin.WebhookDeliveries     TO pol_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON admin.WebhookEndpoints      TO pol_app;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811024015_AdminDeliveryRuntimeGrants'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260811024015_AdminDeliveryRuntimeGrants', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    DROP INDEX [IX_Users_Subject] ON [merch].[Users];
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    DROP INDEX [IX_Users_Subject] ON [admin].[Users];
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    DROP INDEX [IX_RegistrationAudits_TargetSubject] ON [merch].[RegistrationAudits];
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    ALTER TABLE [merch].[Users] ADD [Provider] nvarchar(32) NOT NULL DEFAULT N'google';
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    ALTER TABLE [admin].[Users] ADD [Provider] nvarchar(32) NOT NULL DEFAULT N'google';
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    ALTER TABLE [merch].[RegistrationAudits] ADD [ActorAdminId] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    ALTER TABLE [merch].[RegistrationAudits] ADD [TargetUserId] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    UPDATE ra SET ra.TargetUserId = u.Id
    FROM merch.RegistrationAudits ra
    JOIN merch.Users u ON u.Subject = ra.TargetSubject;
    IF EXISTS (SELECT 1 FROM merch.RegistrationAudits WHERE TargetUserId IS NULL)
        THROW 50001, 'Migration blocked: merch.RegistrationAudits has rows whose TargetSubject matches no merch.Users row - resolve the orphans before upgrading.', 1;

    UPDATE ra SET ra.ActorAdminId = a.Id
    FROM merch.RegistrationAudits ra
    JOIN admin.Users a ON a.Subject = ra.ActorSubject
    WHERE ra.ActorSubject IS NOT NULL;
    IF EXISTS (
        SELECT 1 FROM merch.RegistrationAudits
        WHERE Action IN (N'approved', N'rejected', N'revealed', N'suspended')
          AND ActorAdminId IS NULL)
        THROW 50001, 'Migration blocked: an admin-performed merch.RegistrationAudits row has no matching admin.Users actor - resolve the actor before upgrading.', 1;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    DECLARE @var4 nvarchar(max);
    SELECT @var4 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[merch].[RegistrationAudits]') AND [c].[name] = N'TargetUserId');
    IF @var4 IS NOT NULL EXEC(N'ALTER TABLE [merch].[RegistrationAudits] DROP CONSTRAINT ' + @var4 + ';');
    ALTER TABLE [merch].[RegistrationAudits] ALTER COLUMN [TargetUserId] uniqueidentifier NOT NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_Provider_Subject] ON [merch].[Users] ([Provider], [Subject]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Users_Provider_Subject] ON [admin].[Users] ([Provider], [Subject]) WHERE [Subject] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    CREATE INDEX [IX_RegistrationAudits_TargetUserId] ON [merch].[RegistrationAudits] ([TargetUserId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    ALTER TABLE [merch].[RegistrationAudits] ADD CONSTRAINT [FK_RegistrationAudits_Users_TargetUserId] FOREIGN KEY ([TargetUserId]) REFERENCES [merch].[Users] ([Id]) ON DELETE NO ACTION;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260816162306_MicrosoftOidcProviderDiscriminator'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260816162306_MicrosoftOidcProviderDiscriminator', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD [PaymentProviderId] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    ALTER TABLE [shop].[Orders] ADD [InitiatingAudience] int NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    ALTER TABLE [shop].[Orders] ADD [InitiatingMerchantUserId] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [cfg].[PaymentAuthorizationStates] (
        [Id] uniqueidentifier NOT NULL,
        [Mode] int NOT NULL,
        [CutoffAt] datetime2 NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_PaymentAuthorizationStates] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_PaymentAuthorizationStates_Singleton] CHECK ([Id] = 'f9000000-0000-4000-8000-000000000001')
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [cfg].[PaymentCapabilityMigrationConflicts] (
        [Id] uniqueidentifier NOT NULL,
        [Kind] varchar(64) NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [EntityId] uniqueidentifier NULL,
        [Detail] nvarchar(1000) NOT NULL,
        [DetectedAt] datetime2 NOT NULL,
        [ResolvedAt] datetime2 NULL,
        [ResolvedBy] uniqueidentifier NULL,
        CONSTRAINT [PK_PaymentCapabilityMigrationConflicts] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [cfg].[PaymentMethods] (
        [Id] uniqueidentifier NOT NULL,
        [Code] varchar(32) NOT NULL,
        [Name] nvarchar(120) NOT NULL,
        [IsActive] bit NOT NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_PaymentMethods] PRIMARY KEY ([Id])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [cfg].[PaymentProviders] (
        [Id] uniqueidentifier NOT NULL,
        [Code] varchar(32) NOT NULL,
        [AdapterCode] int NOT NULL,
        [Name] nvarchar(120) NOT NULL,
        [IsEnabled] bit NOT NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_PaymentProviders] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_PaymentProviders_Id_AdapterCode] UNIQUE ([Id], [AdapterCode])
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [txn].[MerchantPaymentMethods] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [PaymentMethodId] uniqueidentifier NOT NULL,
        [IsEnabled] bit NOT NULL,
        [CreatedBy] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_MerchantPaymentMethods] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_MerchantPaymentMethods_MerchantId_PaymentMethodId] UNIQUE ([MerchantId], [PaymentMethodId]),
        CONSTRAINT [FK_MerchantPaymentMethods_Merchants_MerchantId] FOREIGN KEY ([MerchantId]) REFERENCES [merch].[Merchants] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MerchantPaymentMethods_PaymentMethods_PaymentMethodId] FOREIGN KEY ([PaymentMethodId]) REFERENCES [cfg].[PaymentMethods] ([Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [cfg].[PaymentMethodOptionGroups] (
        [Id] uniqueidentifier NOT NULL,
        [PaymentMethodId] uniqueidentifier NOT NULL,
        [Code] varchar(32) NOT NULL,
        [Name] nvarchar(120) NOT NULL,
        CONSTRAINT [PK_PaymentMethodOptionGroups] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_PaymentMethodOptionGroups_Id_PaymentMethodId] UNIQUE ([Id], [PaymentMethodId]),
        CONSTRAINT [FK_PaymentMethodOptionGroups_PaymentMethods_PaymentMethodId] FOREIGN KEY ([PaymentMethodId]) REFERENCES [cfg].[PaymentMethods] ([Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [cfg].[PaymentProviderMethods] (
        [Id] uniqueidentifier NOT NULL,
        [PaymentProviderId] uniqueidentifier NOT NULL,
        [PaymentMethodId] uniqueidentifier NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedBy] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_PaymentProviderMethods] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_PaymentProviderMethods_Id_PaymentMethodId] UNIQUE ([Id], [PaymentMethodId]),
        CONSTRAINT [AK_PaymentProviderMethods_Id_PaymentProviderId_PaymentMethodId] UNIQUE ([Id], [PaymentProviderId], [PaymentMethodId]),
        CONSTRAINT [FK_PaymentProviderMethods_PaymentMethods_PaymentMethodId] FOREIGN KEY ([PaymentMethodId]) REFERENCES [cfg].[PaymentMethods] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PaymentProviderMethods_PaymentProviders_PaymentProviderId] FOREIGN KEY ([PaymentProviderId]) REFERENCES [cfg].[PaymentProviders] ([Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [txn].[MerchantUserPaymentMethods] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantUserId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [PaymentMethodId] uniqueidentifier NOT NULL,
        [IsEnabled] bit NOT NULL,
        [CreatedBy] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_MerchantUserPaymentMethods] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_MerchantUserPaymentMethods_MerchantPaymentMethods_MerchantId_PaymentMethodId] FOREIGN KEY ([MerchantId], [PaymentMethodId]) REFERENCES [txn].[MerchantPaymentMethods] ([MerchantId], [PaymentMethodId]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [cfg].[PaymentMethodOptions] (
        [Id] uniqueidentifier NOT NULL,
        [PaymentMethodId] uniqueidentifier NOT NULL,
        [OptionGroupId] uniqueidentifier NOT NULL,
        [Code] varchar(32) NOT NULL,
        [Name] nvarchar(120) NOT NULL,
        CONSTRAINT [PK_PaymentMethodOptions] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_PaymentMethodOptions_Id_PaymentMethodId] UNIQUE ([Id], [PaymentMethodId]),
        CONSTRAINT [FK_PaymentMethodOptions_PaymentMethodOptionGroups_OptionGroupId_PaymentMethodId] FOREIGN KEY ([OptionGroupId], [PaymentMethodId]) REFERENCES [cfg].[PaymentMethodOptionGroups] ([Id], [PaymentMethodId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PaymentMethodOptions_PaymentMethods_PaymentMethodId] FOREIGN KEY ([PaymentMethodId]) REFERENCES [cfg].[PaymentMethods] ([Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [txn].[MerchantProviderAccountMethods] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [PspConnectionId] uniqueidentifier NOT NULL,
        [PaymentProviderId] uniqueidentifier NOT NULL,
        [PaymentProviderMethodId] uniqueidentifier NOT NULL,
        [PaymentMethodId] uniqueidentifier NOT NULL,
        [IsEnabled] bit NOT NULL,
        [CreatedBy] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_MerchantProviderAccountMethods] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_MerchantProviderAccountMethods_Id_MerchantId_PspConnectionId_PaymentProviderId_PaymentProviderMethodId_PaymentMethodId] UNIQUE ([Id], [MerchantId], [PspConnectionId], [PaymentProviderId], [PaymentProviderMethodId], [PaymentMethodId]),
        CONSTRAINT [FK_MerchantProviderAccountMethods_Merchants_MerchantId] FOREIGN KEY ([MerchantId]) REFERENCES [merch].[Merchants] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MerchantProviderAccountMethods_PaymentMethods_PaymentMethodId] FOREIGN KEY ([PaymentMethodId]) REFERENCES [cfg].[PaymentMethods] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MerchantProviderAccountMethods_PaymentProviderMethods_PaymentProviderMethodId_PaymentProviderId_PaymentMethodId] FOREIGN KEY ([PaymentProviderMethodId], [PaymentProviderId], [PaymentMethodId]) REFERENCES [cfg].[PaymentProviderMethods] ([Id], [PaymentProviderId], [PaymentMethodId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MerchantProviderAccountMethods_PspConnections_MerchantId_PspConnectionId] FOREIGN KEY ([MerchantId], [PspConnectionId]) REFERENCES [txn].[PspConnections] ([MerchantId], [Id]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [cfg].[PaymentProviderMethodOptions] (
        [Id] uniqueidentifier NOT NULL,
        [PaymentProviderMethodId] uniqueidentifier NOT NULL,
        [PaymentMethodId] uniqueidentifier NOT NULL,
        [PaymentMethodOptionId] uniqueidentifier NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedBy] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_PaymentProviderMethodOptions] PRIMARY KEY ([Id]),
        CONSTRAINT [AK_PaymentProviderMethodOptions_Id_PaymentProviderMethodId_PaymentMethodId_PaymentMethodOptionId] UNIQUE ([Id], [PaymentProviderMethodId], [PaymentMethodId], [PaymentMethodOptionId]),
        CONSTRAINT [FK_PaymentProviderMethodOptions_PaymentMethodOptions_PaymentMethodOptionId_PaymentMethodId] FOREIGN KEY ([PaymentMethodOptionId], [PaymentMethodId]) REFERENCES [cfg].[PaymentMethodOptions] ([Id], [PaymentMethodId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PaymentProviderMethodOptions_PaymentProviderMethods_PaymentProviderMethodId_PaymentMethodId] FOREIGN KEY ([PaymentProviderMethodId], [PaymentMethodId]) REFERENCES [cfg].[PaymentProviderMethods] ([Id], [PaymentMethodId]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE TABLE [txn].[MerchantProviderAccountMethodOptions] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [MerchantProviderAccountMethodId] uniqueidentifier NOT NULL,
        [PspConnectionId] uniqueidentifier NOT NULL,
        [PaymentProviderId] uniqueidentifier NOT NULL,
        [PaymentProviderMethodId] uniqueidentifier NOT NULL,
        [PaymentMethodId] uniqueidentifier NOT NULL,
        [PaymentProviderMethodOptionId] uniqueidentifier NOT NULL,
        [PaymentMethodOptionId] uniqueidentifier NOT NULL,
        [IsEnabled] bit NOT NULL,
        [CreatedBy] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NULL,
        [Version] bigint NOT NULL,
        CONSTRAINT [PK_MerchantProviderAccountMethodOptions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_MerchantProviderAccountMethodOptions_MerchantProviderAccountMethods_MerchantProviderAccountMethodId_MerchantId_PspConnection~] FOREIGN KEY ([MerchantProviderAccountMethodId], [MerchantId], [PspConnectionId], [PaymentProviderId], [PaymentProviderMethodId], [PaymentMethodId]) REFERENCES [txn].[MerchantProviderAccountMethods] ([Id], [MerchantId], [PspConnectionId], [PaymentProviderId], [PaymentProviderMethodId], [PaymentMethodId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_MerchantProviderAccountMethodOptions_PaymentProviderMethodOptions_PaymentProviderMethodOptionId_PaymentProviderMethodId_Paym~] FOREIGN KEY ([PaymentProviderMethodOptionId], [PaymentProviderMethodId], [PaymentMethodId], [PaymentMethodOptionId]) REFERENCES [cfg].[PaymentProviderMethodOptions] ([Id], [PaymentProviderMethodId], [PaymentMethodId], [PaymentMethodOptionId]) ON DELETE NO ACTION
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'CutoffAt', N'Mode', N'Version') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentAuthorizationStates]'))
        SET IDENTITY_INSERT [cfg].[PaymentAuthorizationStates] ON;
    EXEC(N'INSERT INTO [cfg].[PaymentAuthorizationStates] ([Id], [CutoffAt], [Mode], [Version])
    VALUES (''f9000000-0000-4000-8000-000000000001'', NULL, 1, CAST(1 AS bigint))');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'CutoffAt', N'Mode', N'Version') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentAuthorizationStates]'))
        SET IDENTITY_INSERT [cfg].[PaymentAuthorizationStates] OFF;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'IsActive', N'Name', N'Version') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentMethods]'))
        SET IDENTITY_INSERT [cfg].[PaymentMethods] ON;
    EXEC(N'INSERT INTO [cfg].[PaymentMethods] ([Id], [Code], [IsActive], [Name], [Version])
    VALUES (''f1000000-0000-4000-8000-000000000001'', ''card'', CAST(1 AS bit), N''Card'', CAST(1 AS bigint)),
    (''f1000000-0000-4000-8000-000000000002'', ''promptpay'', CAST(1 AS bit), N''PromptPay'', CAST(1 AS bigint)),
    (''f1000000-0000-4000-8000-000000000003'', ''installment'', CAST(1 AS bit), N''Installment'', CAST(1 AS bigint))');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'IsActive', N'Name', N'Version') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentMethods]'))
        SET IDENTITY_INSERT [cfg].[PaymentMethods] OFF;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'AdapterCode', N'Code', N'IsEnabled', N'Name', N'Version') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentProviders]'))
        SET IDENTITY_INSERT [cfg].[PaymentProviders] ON;
    EXEC(N'INSERT INTO [cfg].[PaymentProviders] ([Id], [AdapterCode], [Code], [IsEnabled], [Name], [Version])
    VALUES (''f4000000-0000-4000-8000-000000000001'', 1, ''2c2p'', CAST(1 AS bit), N''2C2P'', CAST(1 AS bigint)),
    (''f4000000-0000-4000-8000-000000000002'', 2, ''omise'', CAST(1 AS bit), N''Omise'', CAST(1 AS bigint))');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'AdapterCode', N'Code', N'IsEnabled', N'Name', N'Version') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentProviders]'))
        SET IDENTITY_INSERT [cfg].[PaymentProviders] OFF;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'Name', N'PaymentMethodId') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentMethodOptionGroups]'))
        SET IDENTITY_INSERT [cfg].[PaymentMethodOptionGroups] ON;
    EXEC(N'INSERT INTO [cfg].[PaymentMethodOptionGroups] ([Id], [Code], [Name], [PaymentMethodId])
    VALUES (''f2000000-0000-4000-8000-000000000001'', ''BANK'', N''Bank'', ''f1000000-0000-4000-8000-000000000003'')');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'Name', N'PaymentMethodId') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentMethodOptionGroups]'))
        SET IDENTITY_INSERT [cfg].[PaymentMethodOptionGroups] OFF;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'CreatedAt', N'CreatedBy', N'IsActive', N'PaymentMethodId', N'PaymentProviderId', N'UpdatedAt', N'UpdatedBy', N'Version') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentProviderMethods]'))
        SET IDENTITY_INSERT [cfg].[PaymentProviderMethods] ON;
    EXEC(N'INSERT INTO [cfg].[PaymentProviderMethods] ([Id], [CreatedAt], [CreatedBy], [IsActive], [PaymentMethodId], [PaymentProviderId], [UpdatedAt], [UpdatedBy], [Version])
    VALUES (''f5000000-0000-4000-8000-000000000001'', ''2026-08-17T00:00:00.0000000Z'', ''f9000000-0000-4000-8000-000000000002'', CAST(1 AS bit), ''f1000000-0000-4000-8000-000000000001'', ''f4000000-0000-4000-8000-000000000001'', NULL, NULL, CAST(1 AS bigint)),
    (''f5000000-0000-4000-8000-000000000002'', ''2026-08-17T00:00:00.0000000Z'', ''f9000000-0000-4000-8000-000000000002'', CAST(1 AS bit), ''f1000000-0000-4000-8000-000000000002'', ''f4000000-0000-4000-8000-000000000001'', NULL, NULL, CAST(1 AS bigint)),
    (''f5000000-0000-4000-8000-000000000003'', ''2026-08-17T00:00:00.0000000Z'', ''f9000000-0000-4000-8000-000000000002'', CAST(1 AS bit), ''f1000000-0000-4000-8000-000000000003'', ''f4000000-0000-4000-8000-000000000001'', NULL, NULL, CAST(1 AS bigint)),
    (''f5000000-0000-4000-8000-000000000004'', ''2026-08-17T00:00:00.0000000Z'', ''f9000000-0000-4000-8000-000000000002'', CAST(1 AS bit), ''f1000000-0000-4000-8000-000000000001'', ''f4000000-0000-4000-8000-000000000002'', NULL, NULL, CAST(1 AS bigint))');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'CreatedAt', N'CreatedBy', N'IsActive', N'PaymentMethodId', N'PaymentProviderId', N'UpdatedAt', N'UpdatedBy', N'Version') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentProviderMethods]'))
        SET IDENTITY_INSERT [cfg].[PaymentProviderMethods] OFF;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'Name', N'OptionGroupId', N'PaymentMethodId') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentMethodOptions]'))
        SET IDENTITY_INSERT [cfg].[PaymentMethodOptions] ON;
    EXEC(N'INSERT INTO [cfg].[PaymentMethodOptions] ([Id], [Code], [Name], [OptionGroupId], [PaymentMethodId])
    VALUES (''f3000000-0000-4000-8000-000000000001'', ''KBANK'', N''KBANK'', ''f2000000-0000-4000-8000-000000000001'', ''f1000000-0000-4000-8000-000000000003''),
    (''f3000000-0000-4000-8000-000000000002'', ''SCB'', N''SCB'', ''f2000000-0000-4000-8000-000000000001'', ''f1000000-0000-4000-8000-000000000003''),
    (''f3000000-0000-4000-8000-000000000003'', ''KTC'', N''KTC'', ''f2000000-0000-4000-8000-000000000001'', ''f1000000-0000-4000-8000-000000000003''),
    (''f3000000-0000-4000-8000-000000000004'', ''BAY'', N''BAY'', ''f2000000-0000-4000-8000-000000000001'', ''f1000000-0000-4000-8000-000000000003'')');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'Name', N'OptionGroupId', N'PaymentMethodId') AND [object_id] = OBJECT_ID(N'[cfg].[PaymentMethodOptions]'))
        SET IDENTITY_INSERT [cfg].[PaymentMethodOptions] OFF;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    EXEC(N'ALTER TABLE [merch].[Users] ADD CONSTRAINT [CK_Users_ActorMerchant] CHECK ([Status] NOT IN (2, 4) OR ([MerchantId] IS NOT NULL AND [MerchantId] <> ''00000000-0000-0000-0000-000000000000''))');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_PspConnections_MerchantId_PaymentProviderId] ON [txn].[PspConnections] ([MerchantId], [PaymentProviderId]) WHERE [PaymentProviderId] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_PspConnections_PaymentProviderId_Psp] ON [txn].[PspConnections] ([PaymentProviderId], [Psp]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_Orders_InitiatingMerchantUserId_MerchantId] ON [shop].[Orders] ([InitiatingMerchantUserId], [MerchantId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    EXEC(N'ALTER TABLE [shop].[Orders] ADD CONSTRAINT [CK_Orders_InitiatingAudience] CHECK (([InitiatingAudience] IS NULL AND [InitiatingMerchantUserId] IS NULL) OR ([InitiatingAudience] = 1 AND [InitiatingMerchantUserId] IS NOT NULL AND [OriginatorId] IS NULL) OR ([InitiatingAudience] = 2 AND [InitiatingMerchantUserId] IS NULL AND [OriginatorId] IS NOT NULL))');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    EXEC(N'ALTER TABLE [shop].[Orders] ADD CONSTRAINT [CK_Orders_PaymentChannel_Canonical] CHECK ([PaymentChannel] IS NULL OR [PaymentChannel] IN (''card'', ''promptpay'', ''installment''))');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_MerchantPaymentMethods_PaymentMethodId] ON [txn].[MerchantPaymentMethods] ([PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_MerchantProviderAccountMethodOptions_MerchantProviderAccountMethodId_MerchantId_PspConnectionId_PaymentProviderId_PaymentPro~] ON [txn].[MerchantProviderAccountMethodOptions] ([MerchantProviderAccountMethodId], [MerchantId], [PspConnectionId], [PaymentProviderId], [PaymentProviderMethodId], [PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantProviderAccountMethodOptions_MerchantProviderAccountMethodId_PaymentMethodOptionId] ON [txn].[MerchantProviderAccountMethodOptions] ([MerchantProviderAccountMethodId], [PaymentMethodOptionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_MerchantProviderAccountMethodOptions_PaymentProviderMethodOptionId_PaymentProviderMethodId_PaymentMethodId_PaymentMethodOpti~] ON [txn].[MerchantProviderAccountMethodOptions] ([PaymentProviderMethodOptionId], [PaymentProviderMethodId], [PaymentMethodId], [PaymentMethodOptionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_MerchantProviderAccountMethods_MerchantId_PspConnectionId] ON [txn].[MerchantProviderAccountMethods] ([MerchantId], [PspConnectionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_MerchantProviderAccountMethods_PaymentMethodId] ON [txn].[MerchantProviderAccountMethods] ([PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_MerchantProviderAccountMethods_PaymentProviderMethodId_PaymentProviderId_PaymentMethodId] ON [txn].[MerchantProviderAccountMethods] ([PaymentProviderMethodId], [PaymentProviderId], [PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantProviderAccountMethods_PspConnectionId_PaymentMethodId] ON [txn].[MerchantProviderAccountMethods] ([PspConnectionId], [PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_MerchantUserPaymentMethods_MerchantId_PaymentMethodId] ON [txn].[MerchantUserPaymentMethods] ([MerchantId], [PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantUserPaymentMethods_MerchantUserId_PaymentMethodId] ON [txn].[MerchantUserPaymentMethods] ([MerchantUserId], [PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_PaymentCapabilityMigrationConflicts_ResolvedAt_Kind] ON [cfg].[PaymentCapabilityMigrationConflicts] ([ResolvedAt], [Kind]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PaymentMethodOptionGroups_PaymentMethodId_Code] ON [cfg].[PaymentMethodOptionGroups] ([PaymentMethodId], [Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PaymentMethodOptions_OptionGroupId_Code] ON [cfg].[PaymentMethodOptions] ([OptionGroupId], [Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_PaymentMethodOptions_OptionGroupId_PaymentMethodId] ON [cfg].[PaymentMethodOptions] ([OptionGroupId], [PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_PaymentMethodOptions_PaymentMethodId] ON [cfg].[PaymentMethodOptions] ([PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PaymentMethods_Code] ON [cfg].[PaymentMethods] ([Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_PaymentProviderMethodOptions_PaymentMethodOptionId_PaymentMethodId] ON [cfg].[PaymentProviderMethodOptions] ([PaymentMethodOptionId], [PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_PaymentProviderMethodOptions_PaymentProviderMethodId_PaymentMethodId] ON [cfg].[PaymentProviderMethodOptions] ([PaymentProviderMethodId], [PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PaymentProviderMethodOptions_PaymentProviderMethodId_PaymentMethodOptionId] ON [cfg].[PaymentProviderMethodOptions] ([PaymentProviderMethodId], [PaymentMethodOptionId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE INDEX [IX_PaymentProviderMethods_PaymentMethodId] ON [cfg].[PaymentProviderMethods] ([PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PaymentProviderMethods_PaymentProviderId_PaymentMethodId] ON [cfg].[PaymentProviderMethods] ([PaymentProviderId], [PaymentMethodId]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PaymentProviders_AdapterCode] ON [cfg].[PaymentProviders] ([AdapterCode]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PaymentProviders_Code] ON [cfg].[PaymentProviders] ([Code]);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD CONSTRAINT [FK_PspConnections_Merchants_MerchantId] FOREIGN KEY ([MerchantId]) REFERENCES [merch].[Merchants] ([Id]) ON DELETE NO ACTION;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    ALTER TABLE [txn].[PspConnections] ADD CONSTRAINT [FK_PspConnections_PaymentProviders_PaymentProviderId_Psp] FOREIGN KEY ([PaymentProviderId], [Psp]) REFERENCES [cfg].[PaymentProviders] ([Id], [AdapterCode]) ON DELETE NO ACTION;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    ALTER TABLE merch.Users ADD CONSTRAINT UQ_Users_Id_MerchantId
        UNIQUE (Id, MerchantId);
    ALTER TABLE txn.PspConnections ADD CONSTRAINT UQ_PspConnections_Id_MerchantId_PaymentProviderId
        UNIQUE (Id, MerchantId, PaymentProviderId);

    ALTER TABLE txn.MerchantUserPaymentMethods ADD CONSTRAINT FK_MerchantUserPaymentMethods_Users_User_Merchant
        FOREIGN KEY (MerchantUserId, MerchantId)
        REFERENCES merch.Users (Id, MerchantId);
    ALTER TABLE txn.MerchantProviderAccountMethods ADD CONSTRAINT FK_MerchantProviderAccountMethods_PspConnections_Account_Provider
        FOREIGN KEY (PspConnectionId, MerchantId, PaymentProviderId)
        REFERENCES txn.PspConnections (Id, MerchantId, PaymentProviderId);
    ALTER TABLE shop.Orders ADD CONSTRAINT FK_Orders_Users_Initiator_Merchant
        FOREIGN KEY (InitiatingMerchantUserId, MerchantId)
        REFERENCES merch.Users (Id, MerchantId);
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    IF USER_ID(N'pol_app') IS NULL
        THROW 51003, N'MerchantUserPaymentMethodAccessExpand requires database user [pol_app].', 1;

    GRANT SELECT, INSERT, UPDATE ON cfg.PaymentMethods TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON cfg.PaymentMethodOptionGroups TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON cfg.PaymentMethodOptions TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON cfg.PaymentProviders TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON cfg.PaymentProviderMethods TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON cfg.PaymentProviderMethodOptions TO pol_app;
    GRANT SELECT, UPDATE ON cfg.PaymentAuthorizationStates TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON cfg.PaymentCapabilityMigrationConflicts TO pol_app;

    GRANT SELECT, INSERT, UPDATE ON txn.MerchantProviderAccountMethods TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON txn.MerchantProviderAccountMethodOptions TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON txn.MerchantPaymentMethods TO pol_app;
    GRANT SELECT, INSERT, UPDATE ON txn.MerchantUserPaymentMethods TO pol_app;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817170326_MerchantUserPaymentMethodAccessExpand'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260817170326_MerchantUserPaymentMethodAccessExpand', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817172338_MerchantPaymentCapabilityControlPlane'
)
BEGIN
    ALTER TABLE [cfg].[PaymentProviders] ADD [UpdatedAt] datetime2 NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817172338_MerchantPaymentCapabilityControlPlane'
)
BEGIN
    ALTER TABLE [cfg].[PaymentProviders] ADD [UpdatedBy] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817172338_MerchantPaymentCapabilityControlPlane'
)
BEGIN
    ALTER TABLE [cfg].[PaymentMethods] ADD [UpdatedAt] datetime2 NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817172338_MerchantPaymentCapabilityControlPlane'
)
BEGIN
    ALTER TABLE [cfg].[PaymentMethods] ADD [UpdatedBy] uniqueidentifier NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817172338_MerchantPaymentCapabilityControlPlane'
)
BEGIN
    EXEC(N'UPDATE [cfg].[PaymentMethods] SET [UpdatedAt] = NULL, [UpdatedBy] = NULL
    WHERE [Id] = ''f1000000-0000-4000-8000-000000000001'';
    SELECT @@ROWCOUNT');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817172338_MerchantPaymentCapabilityControlPlane'
)
BEGIN
    EXEC(N'UPDATE [cfg].[PaymentMethods] SET [UpdatedAt] = NULL, [UpdatedBy] = NULL
    WHERE [Id] = ''f1000000-0000-4000-8000-000000000002'';
    SELECT @@ROWCOUNT');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817172338_MerchantPaymentCapabilityControlPlane'
)
BEGIN
    EXEC(N'UPDATE [cfg].[PaymentMethods] SET [UpdatedAt] = NULL, [UpdatedBy] = NULL
    WHERE [Id] = ''f1000000-0000-4000-8000-000000000003'';
    SELECT @@ROWCOUNT');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817172338_MerchantPaymentCapabilityControlPlane'
)
BEGIN
    EXEC(N'UPDATE [cfg].[PaymentProviders] SET [UpdatedAt] = NULL, [UpdatedBy] = NULL
    WHERE [Id] = ''f4000000-0000-4000-8000-000000000001'';
    SELECT @@ROWCOUNT');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817172338_MerchantPaymentCapabilityControlPlane'
)
BEGIN
    EXEC(N'UPDATE [cfg].[PaymentProviders] SET [UpdatedAt] = NULL, [UpdatedBy] = NULL
    WHERE [Id] = ''f4000000-0000-4000-8000-000000000002'';
    SELECT @@ROWCOUNT');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260817172338_MerchantPaymentCapabilityControlPlane'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260817172338_MerchantPaymentCapabilityControlPlane', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819145219_WorkforceTenantBinding'
)
BEGIN
    IF EXISTS (
        SELECT 1
        FROM admin.Users
        WHERE Provider = N'microsoft'
          AND Subject IS NOT NULL
          AND (
              DATALENGTH(Subject) <> 72
              OR TRY_CONVERT(uniqueidentifier, Subject) IS NULL
              OR Subject COLLATE Latin1_General_100_CI_AS
                 <> CONVERT(nvarchar(36), TRY_CONVERT(uniqueidentifier, Subject))
                    COLLATE Latin1_General_100_CI_AS
          )
    )
        THROW 50000, 'Microsoft admin subjects must be exact UUID D values before workforce tenant migration.', 1;

    IF EXISTS (
        SELECT ConvertedSubject
        FROM (
            SELECT TRY_CONVERT(uniqueidentifier, Subject) AS ConvertedSubject
            FROM admin.Users
            WHERE Provider = N'microsoft' AND Subject IS NOT NULL
        ) valuesToCheck
        GROUP BY ConvertedSubject
        HAVING COUNT(*) > 1
    )
        THROW 50000, 'Duplicate Microsoft admin identities block workforce tenant migration.', 1;

    UPDATE admin.Users
    SET Subject = LOWER(CONVERT(nvarchar(36), TRY_CONVERT(uniqueidentifier, Subject)))
    WHERE Provider = N'microsoft' AND Subject IS NOT NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819145219_WorkforceTenantBinding'
)
BEGIN
    CREATE TABLE [admin].[WorkforceTenantBindings] (
        [Id] tinyint NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_WorkforceTenantBindings] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_WorkforceTenantBindings_Singleton] CHECK ([Id] = 1)
    );
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819145219_WorkforceTenantBinding'
)
BEGIN
    GRANT SELECT, INSERT ON admin.WorkforceTenantBindings TO pol_app;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819145219_WorkforceTenantBinding'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260819145219_WorkforceTenantBinding', N'10.0.8');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260823132337_Tier0WorkforceEmailIdentity'
)
BEGIN
    ALTER TABLE [admin].[Users] ADD [WorkforceEmailKey] nvarchar(254) NULL;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260823132337_Tier0WorkforceEmailIdentity'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Users_WorkforceEmailKey] ON [admin].[Users] ([WorkforceEmailKey]) WHERE [WorkforceEmailKey] IS NOT NULL');
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260823132337_Tier0WorkforceEmailIdentity'
)
BEGIN
    CREATE TABLE admin.WorkforceIdentityMigrations
    (
        Id int NOT NULL,
        CompletedAt datetime2(7) NULL,
        SnapshotCount int NOT NULL CONSTRAINT DF_WorkforceIdentityMigrations_SnapshotCount DEFAULT 0,
        ConvertedCount int NOT NULL CONSTRAINT DF_WorkforceIdentityMigrations_ConvertedCount DEFAULT 0,
        NoOpCount int NOT NULL CONSTRAINT DF_WorkforceIdentityMigrations_NoOpCount DEFAULT 0,
        CONSTRAINT PK_WorkforceIdentityMigrations PRIMARY KEY (Id),
        CONSTRAINT CK_WorkforceIdentityMigrations_Singleton
            CHECK (Id = 1 AND SnapshotCount >= 0 AND ConvertedCount >= 0 AND NoOpCount >= 0)
    );

    CREATE TABLE admin.WorkforceIdentitySubjectRollback
    (
        AdminUserId uniqueidentifier NOT NULL,
        LegacySubject nvarchar(256) NULL,
        CanonicalSubject nvarchar(254) NULL,
        ConversionKind nvarchar(16) NULL,
        CONSTRAINT PK_WorkforceIdentitySubjectRollback PRIMARY KEY (AdminUserId),
        CONSTRAINT FK_WorkforceIdentitySubjectRollback_Users_AdminUserId
            FOREIGN KEY (AdminUserId) REFERENCES admin.Users (Id)
    );

    INSERT admin.WorkforceIdentitySubjectRollback (AdminUserId, LegacySubject)
    SELECT Id, Subject
    FROM admin.Users
    WHERE Provider COLLATE Latin1_General_100_BIN2 = N'microsoft'
      AND Subject IS NOT NULL;

    INSERT admin.WorkforceIdentityMigrations
        (Id, CompletedAt, SnapshotCount, ConvertedCount, NoOpCount)
    SELECT 1, NULL, COUNT(*), 0, 0
    FROM admin.WorkforceIdentitySubjectRollback;

    GRANT SELECT ON admin.WorkforceIdentityMigrations TO pol_app;
END;

GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260823132337_Tier0WorkforceEmailIdentity'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260823132337_Tier0WorkforceEmailIdentity', N'10.0.8');
END;

COMMIT;
GO

