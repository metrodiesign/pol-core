-- Local/dev e2e reset: unwind one agent registration so the same identity can register again.
-- After Approve (AgentRegistrationStore.ApproveAsync) the identity owns acct.Accounts + LoginAccounts +
-- Agents + access.MerchantAccess + AccessRoles, and Submit answers 409 account_already_approved forever.
-- Deletes that graph in FK order inside one transaction; admin.GovernanceOutboxMessages is kept (audit).
-- Never run against production.
--
-- sqlcmd variable: RegistrationId (acct.AgentRegistrations.Id).
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON; -- filtered indexes on the access tables reject DELETE otherwise

DECLARE @reg UNIQUEIDENTIFIER = '$(RegistrationId)';
IF NOT EXISTS (SELECT 1 FROM acct.AgentRegistrations WHERE Id = @reg)
    THROW 50000, 'registration not found', 1;

DECLARE @agents TABLE (AccountId UNIQUEIDENTIFIER);
INSERT @agents
SELECT DISTINCT l.AccountId
FROM acct.AgentRegistrationAttempts att
JOIN acct.LoginAccounts l
    ON l.Provider = att.Provider AND l.TenantId = att.TenantId AND l.ExternalUserId = att.ExternalUserId
JOIN acct.Accounts a ON a.Id = l.AccountId AND a.AccountType = 2 -- AccountType.Agent
WHERE att.RegistrationId = @reg;

BEGIN TRAN;
DELETE r FROM access.AccessRoles r
    JOIN access.MerchantAccess m ON m.Id = r.MerchantAccessId
    WHERE m.AccountId IN (SELECT AccountId FROM @agents);
DELETE FROM access.MerchantAccess WHERE AccountId IN (SELECT AccountId FROM @agents);
DELETE FROM acct.Agents WHERE AccountId IN (SELECT AccountId FROM @agents);
DELETE FROM acct.LoginAccounts WHERE AccountId IN (SELECT AccountId FROM @agents);
DELETE FROM acct.Accounts WHERE Id IN (SELECT AccountId FROM @agents);
DELETE FROM acct.AgentRegistrationAttempts WHERE RegistrationId = @reg;
DELETE FROM acct.AgentRegistrations WHERE Id = @reg;
COMMIT;

SELECT 'agent accounts removed' AS what, COUNT(*) AS n FROM @agents;
