-- Local/dev bootstrap: give an Employee account a Platform role so /me/access carries real permissions.
-- Mirrors IdentityAccessStore.ReplacePlatformAccessAsync (access.PlatformAccess + PlatformAccessRoles +
-- authorization version bump) for the first employee, who has no admin session to call
-- PUT /api/v1/accounts/{id}/platform-access with. Never run against production: the audited path is the API.
--
-- sqlcmd variables: AccountId (acct.Accounts.Id of an Employee), RoleCode (iam.Roles.Code, Platform or Shared scope).
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @account UNIQUEIDENTIFIER = '$(AccountId)';
DECLARE @roleCode NVARCHAR(64) = N'$(RoleCode)';

IF NOT EXISTS (SELECT 1 FROM acct.Accounts WHERE Id = @account AND AccountType = 1)
    THROW 50001, 'AccountId is not an Employee account.', 1;

DECLARE @role UNIQUEIDENTIFIER, @roleScope INT;
SELECT @role = Id, @roleScope = Scope
FROM iam.Roles
WHERE Code = @roleCode AND Scope IN (1, 3) AND MerchantId IS NULL;
IF @role IS NULL
    THROW 50002, 'RoleCode is not a Platform/Shared role.', 1;

BEGIN TRANSACTION;

DECLARE @access UNIQUEIDENTIFIER = (SELECT Id FROM access.PlatformAccess WHERE EmployeeAccountId = @account);
IF @access IS NULL
BEGIN
    SET @access = NEWID();
    INSERT INTO access.PlatformAccess (Id, EmployeeAccountId, Status, Version)
    VALUES (@access, @account, 1, 1);
END
ELSE
    UPDATE access.PlatformAccess SET Status = 1, Version = Version + 1 WHERE Id = @access;

IF NOT EXISTS (SELECT 1 FROM access.PlatformAccessRoles WHERE PlatformAccessId = @access AND RoleId = @role)
    INSERT INTO access.PlatformAccessRoles (Id, PlatformAccessId, RoleId, RoleScope)
    VALUES (NEWID(), @access, @role, @roleScope);

-- Bump like Account.BumpAuthorizationVersion: live BFF sessions become stale and the employee logs in again.
UPDATE acct.Accounts
SET AuthorizationVersion = AuthorizationVersion + 1, UpdatedAt = SYSUTCDATETIME()
WHERE Id = @account;

COMMIT TRANSACTION;

SELECT a.Id AS AccountId, a.DisplayName, a.AuthorizationVersion, pa.Status AS PlatformAccessStatus,
       r.Code AS RoleCode, (SELECT COUNT(*) FROM iam.RolePermissions WHERE RoleId = r.Id) AS PermissionCount
FROM acct.Accounts a
JOIN access.PlatformAccess pa ON pa.EmployeeAccountId = a.Id
JOIN access.PlatformAccessRoles par ON par.PlatformAccessId = pa.Id
JOIN iam.Roles r ON r.Id = par.RoleId
WHERE a.Id = @account;
