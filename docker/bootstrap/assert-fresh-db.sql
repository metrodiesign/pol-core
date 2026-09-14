-- SQL Server 2025 fresh-database verification. Run after all EF migrations.
-- sqlcmd -S <server> -U sa -P <pw> -C -b -v DbName=VCentralPay -i assert-fresh-db.sql
SET NOCOUNT ON;
USE [$(DbName)];
GO

DECLARE @fail nvarchar(max) = N'';
DECLARE @major int = TRY_CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));
DECLARE @version nvarchar(128) = CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));
DECLARE @build int = TRY_CONVERT(int, PARSENAME(@version, 2));
DECLARE @revision int = TRY_CONVERT(int, PARSENAME(@version, 1));

IF @major <> 17 OR @build < 4045 OR (@build = 4045 AND @revision < 5)
    SET @fail += N'engine must be SQL Server 2025 CU5 (17.0.4045.5) or newer; ';
IF (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) <> 170
    SET @fail += N'database compatibility level must be 170; ';
IF ISNULL(CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), N'Collation')), N'') <> N'Thai_100_CI_AS'
    SET @fail += N'database collation must be Thai_100_CI_AS; ';

IF (SELECT COUNT(*) FROM sys.schemas s
    JOIN sys.database_principals dp ON dp.principal_id = s.principal_id
    WHERE s.name IN (N'access', N'acct', N'admin', N'cfg', N'checkout', N'iam', N'merch', N'oauth', N'shop', N'txn')
      AND dp.name = N'dbo') <> 10
    SET @fail += N'application schemas must be ten and owned by dbo; ';

DECLARE @expectedMigrations TABLE (MigrationId nvarchar(150) PRIMARY KEY);
INSERT INTO @expectedMigrations (MigrationId) VALUES
    (N'20260807042818_InitialSchema'),
    (N'20260807042828_SecurityObjects'),
    (N'20260807042833_SeedData'),
    (N'20260808161508_OneBasedPersistedEnumStorage'),
    (N'20260809183210_MerchantRealApiIdentity'),
    (N'20260810041211_AdminConsolePermissionKeys'),
    (N'20260810055607_GovernanceFoundation'),
    (N'20260810055818_GovernancePlatformHeadUniqueness'),
    (N'20260810074055_AdminConsoleResourceVersions'),
    (N'20260810112718_AdminTenantPspRoutingControlPlane'),
    (N'20260810133139_AdminMerchantIdentityControl'),
    (N'20260810150130_AdminCommerceLifecycle'),
    (N'20260810153008_AdminCommerceUpdatedAtDefault'),
    (N'20260810162000_AdminCommerceOperationUpdateGrant'),
    (N'20260810184403_AdminDeliveryControlAndInboundWebhook'),
    (N'20260811024015_AdminDeliveryRuntimeGrants'),
    (N'20260816162306_MicrosoftOidcProviderDiscriminator'),
    (N'20260817170326_MerchantUserPaymentMethodAccessExpand'),
    (N'20260817172338_MerchantPaymentCapabilityControlPlane'),
    (N'20260819145219_WorkforceTenantBinding'),
    (N'20260823132337_Tier0WorkforceEmailIdentity'),
    (N'20260830172117_Tier0EmployeeProfile'),
    (N'20260902133906_Tier0MicrosoftTenantAwareIdentity'),
    (N'20260905104644_DropOrgReferenceMasterData'),
    (N'20260906025013_SwitchProviderDefaultToMicrosoft'),
    (N'20260906121417_MerchantPspSecurityFoundation'),
    (N'20260906123608_MerchantPaymentEnvironment'),
    (N'20260906143227_PaymentSessionRoutingSnapshot'),
    (N'20260906151900_SharedRoleScope'),
    (N'20260907023022_InboundWebhookPendingMatch'),
    (N'20260907033444_LegacyVaultExpiryRemediation'),
    (N'20260910021908_Task2IdentityAccess'),
    (N'20260910031005_Task2AccessReferenceGuards'),
    (N'20260910031241_Task2PlatformRoleScope'),
    (N'20260910031648_Task2AgentSaleMerchantGuard'),
    (N'20260910032407_Task2AccountAuthorizationLease'),
    (N'20260910035334_Task3MerchantMaster'),
    (N'20260910044722_Task4AgentRegistration'),
    (N'20260910060757_Task5OrdersLinks'),
    (N'20260910075921_Task6Transactions'),
    (N'20260910094927_Task7NotificationRuntime'),
    (N'20260910101508_Task7WebhookEndpointUniquenessLive'),
    (N'20260910121500_Task8RuntimeGrants'),
    (N'20260910124500_Task8CommerceRuntimeGrants'),
    (N'20260910140000_Task9MigrationReadiness'),
    (N'20260911160508_ReviewFixOrderVersionedMetadata'),
    (N'20260911163519_ReviewFixPaymentLinkNotificationIntent'),
    (N'20260913174013_RetireBffSessionTickets'),
    (N'20260914051532_RetireAdminSessions'),
    (N'20260914111802_RetireLegacyAdminIdentityPlane');

IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory) <> 50
   OR EXISTS (
       SELECT MigrationId FROM @expectedMigrations
       EXCEPT
       SELECT MigrationId FROM dbo.__EFMigrationsHistory)
   OR EXISTS (
       SELECT MigrationId FROM dbo.__EFMigrationsHistory
       EXCEPT
       SELECT MigrationId FROM @expectedMigrations)
    SET @fail += N'migration history must contain exactly 50 expected migrations through RetireLegacyAdminIdentityPlane; ';

IF OBJECT_ID(N'merch.RegistrationNotices', N'U') IS NULL
    SET @fail += N'merch.RegistrationNotices missing; ';
IF OBJECT_ID(N'shop.OrderNoSeq', N'SO') IS NULL
    SET @fail += N'shop.OrderNoSeq missing; ';
IF OBJECT_ID(N'admin.Users', N'U') IS NOT NULL
   OR OBJECT_ID(N'admin.WorkforceTenantBindings', N'U') IS NOT NULL
   OR OBJECT_ID(N'admin.WorkforceIdentityMigrations', N'U') IS NOT NULL
   OR OBJECT_ID(N'admin.WorkforceIdentitySubjectRollback', N'U') IS NOT NULL
   OR OBJECT_ID(N'admin.WorkforceTenantIdentityMigrations', N'U') IS NOT NULL
   OR OBJECT_ID(N'admin.WorkforceTenantIdentitySnapshot', N'U') IS NOT NULL
   OR OBJECT_ID(N'admin.RoleAssignments', N'U') IS NOT NULL
   OR OBJECT_ID(N'admin.MerchantAccess', N'U') IS NOT NULL
   OR OBJECT_ID(N'admin.AuthAudits', N'U') IS NOT NULL
    SET @fail += N'retired legacy admin identity tables still exist; ';

IF OBJECT_ID(N'shop.CheckoutSessions', N'U') IS NOT NULL
   OR OBJECT_ID(N'shop.OrderItemPolicies', N'U') IS NOT NULL
   OR OBJECT_ID(N'shop.OrderItemPolicyAudits', N'U') IS NOT NULL
   OR OBJECT_ID(N'shop.Products', N'U') IS NOT NULL
    SET @fail += N'retired Checkout/policy/catalogue tables still exist; ';

DECLARE @nativeJsonCount int = (
    SELECT COUNT(*)
    FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE ty.name = N'json'
);
IF @nativeJsonCount <> 11
    SET @fail += N'exactly eleven native json columns required; ';
IF (SELECT COUNT(*)
    FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE ty.name = N'json'
      AND CONCAT(s.name, N'.', t.name, N'.', c.name) IN
          (N'admin.ProvisioningOperations.Result', N'merch.UserOutbox.Payload',
           N'merch.Merchants.Metadata', N'shop.CartItems.Metadata', N'shop.OrderItems.Metadata',
           N'acct.Agents.Metadata', N'acct.Employees.Metadata',
           N'acct.AgentRegistrations.ProfileJson', N'acct.AgentRegistrationAttempts.ProfileJson',
           N'shop.Orders.Metadata', N'shop.OrderItems.RequestMetadata')) <> 11
    SET @fail += N'native json column allowlist mismatch; ';

IF COL_LENGTH(N'shop.Orders', N'Metadata') IS NULL
   OR COL_LENGTH(N'shop.OrderItems', N'RequestMetadata') IS NULL
   OR COL_LENGTH(N'shop.Orders', N'NotificationEmail') IS NULL
   OR COL_LENGTH(N'shop.Orders', N'NotificationPhoneNumber') IS NULL
   OR COL_LENGTH(N'shop.Orders', N'NotifyOnIssue') IS NULL
    SET @fail += N'reviewfix Order metadata/notification columns missing; ';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'shop.Orders')
               AND name = N'IX_Orders_OrderNo' AND is_unique = 1)
   OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'txn.PaymentSessions')
                  AND name = N'IX_PaymentSessions_OrderId_Open' AND is_unique = 1
                  AND filter_definition IS NOT NULL)
   OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'shop.OrderItems')
                  AND name = N'IX_OrderItems_ProductCode')
    SET @fail += N'required commerce indexes missing; ';

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CartItems_Carts_CartId_MerchantId')
   OR NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_OrderItems_Orders_OrderId_MerchantId')
   OR NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_RolePermissions_Permissions_PermissionKey')
    SET @fail += N'required foreign keys missing; ';
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Roles_ScopeMerchant')
    SET @fail += N'role scope check constraint missing; ';

IF USER_ID(N'pol_app') IS NULL
    SET @fail += N'pol_app database user missing; ';
IF NOT EXISTS (SELECT 1 FROM sys.database_permissions p
               WHERE p.grantee_principal_id = USER_ID(N'pol_app')
                 AND p.major_id = OBJECT_ID(N'shop.Orders') AND p.permission_name = N'SELECT' AND p.state = N'G')
   OR NOT EXISTS (SELECT 1 FROM sys.database_permissions p
                  WHERE p.grantee_principal_id = USER_ID(N'pol_app')
                    AND p.major_id = OBJECT_ID(N'shop.Orders') AND p.permission_name = N'INSERT' AND p.state = N'G')
   OR NOT EXISTS (SELECT 1 FROM sys.database_permissions p
                  WHERE p.grantee_principal_id = USER_ID(N'pol_app')
                    AND p.major_id = OBJECT_ID(N'shop.OrderNoSeq') AND p.permission_name = N'UPDATE' AND p.state = N'G')
   OR NOT EXISTS (SELECT 1 FROM sys.database_permissions p
                  WHERE p.grantee_principal_id = USER_ID(N'pol_app')
                    AND p.major_id = OBJECT_ID(N'merch.RegistrationNotices') AND p.permission_name = N'INSERT' AND p.state = N'G')
   OR NOT EXISTS (SELECT 1 FROM sys.database_permissions p
                  WHERE p.grantee_principal_id = USER_ID(N'pol_app')
                    AND p.major_id = OBJECT_ID(N'admin.WorkforceTenantBindings') AND p.permission_name = N'SELECT' AND p.state = N'G')
   OR NOT EXISTS (SELECT 1 FROM sys.database_permissions p
                  WHERE p.grantee_principal_id = USER_ID(N'pol_app')
                    AND p.major_id = OBJECT_ID(N'admin.WorkforceTenantBindings') AND p.permission_name = N'INSERT' AND p.state = N'G')
   OR NOT EXISTS (SELECT 1 FROM sys.database_permissions p
                  WHERE p.grantee_principal_id = USER_ID(N'pol_app')
                    AND p.major_id = OBJECT_ID(N'admin.WorkforceTenantIdentityMigrations')
                    AND p.permission_name = N'SELECT' AND p.state = N'G')
   OR NOT EXISTS (SELECT 1 FROM sys.database_permissions p
                  WHERE p.grantee_principal_id = USER_ID(N'pol_app')
                    AND p.major_id = OBJECT_ID(N'admin.WorkforceTenantIdentitySnapshot')
                    AND p.permission_name = N'SELECT' AND p.state = N'G')
    SET @fail += N'pol_app required grant matrix incomplete; ';
IF EXISTS (SELECT 1 FROM sys.database_permissions p
           WHERE p.grantee_principal_id = USER_ID(N'pol_app')
             AND p.major_id = OBJECT_ID(N'merch.VaultRevealAudits')
             AND p.permission_name IN (N'UPDATE', N'DELETE') AND p.state IN (N'G', N'W'))
    SET @fail += N'append-only vault audit grants widened; ';
IF EXISTS (SELECT 1 FROM sys.database_permissions p
           WHERE p.grantee_principal_id = USER_ID(N'pol_app')
             AND p.major_id = OBJECT_ID(N'admin.WorkforceTenantBindings')
             AND p.permission_name IN (N'UPDATE', N'DELETE') AND p.state IN (N'G', N'W'))
    SET @fail += N'workforce tenant binding grants widened; ';

IF (SELECT COUNT(*) FROM iam.PermissionGroups) <> 7
    SET @fail += N'iam.PermissionGroups expected 7 rows; ';
IF (SELECT COUNT(*) FROM iam.Permissions) <> 25
    SET @fail += N'iam.Permissions expected 25 rows; ';
IF (SELECT COUNT(*) FROM iam.Roles) <> 4
    SET @fail += N'iam.Roles expected 4 rows; ';
IF (SELECT COUNT(*) FROM iam.RolePermissions) <> 36
    SET @fail += N'iam.RolePermissions expected 36 rows; ';
IF EXISTS (SELECT 1 FROM iam.PermissionGroups WHERE Status <> 1)
   OR EXISTS (SELECT 1 FROM iam.Permissions WHERE Status <> 1)
   OR EXISTS (SELECT 1 FROM iam.Roles WHERE Status <> 1)
    SET @fail += N'IAM bootstrap rows must be Active; ';

IF OBJECT_ID(N'cfg.Positions', N'U') IS NOT NULL
   OR OBJECT_ID(N'cfg.Offices', N'U') IS NOT NULL
   OR OBJECT_ID(N'cfg.Levels', N'U') IS NOT NULL
   OR OBJECT_ID(N'cfg.Divisions', N'U') IS NOT NULL
    SET @fail += N'retired cfg org reference tables must not exist; ';

IF (SELECT COUNT(*) FROM merch.Merchants WHERE Id = 'e1000000-0000-4000-8000-000000000001') <> 1
   OR (SELECT COUNT(*) FROM txn.PspConnections WHERE Id = 'e8000000-0000-4000-8000-000000000001'
       AND IsEnabled = 0) <> 1
    SET @fail += N'supported synthetic demo merchant/PSP seed missing; ';

IF LEN(@fail) > 0
    THROW 50000, @fail, 1;

PRINT N'assert-fresh-db: OK';
GO
