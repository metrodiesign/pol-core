using Microsoft.Data.SqlClient;
using Accounts.Application;

namespace Integration.Tests;

[Collection("IdentityAccessSql")]
[Trait("Capability", "IdentityAccess")]
public sealed class IdentityAccessAccessPersistenceTests
{
    [Fact]
    [Trait("Requirement", "REQ-3.1")]
    [Trait("Requirement", "REQ-3.2")]
    [Trait("Requirement", "REQ-3.3")]
    [Trait("Requirement", "REQ-3.5")]
    [Trait("Requirement", "REQ-3.12")]
    public async Task Sql_access_constraints_enforce_scope_cardinality_merchant_binding_and_employee_platform_access()
    {
        var accountId = Guid.NewGuid();
        var agentAccountId = Guid.NewGuid();
        var employeeAccountId = Guid.NewGuid();
        var accessId = Guid.NewGuid();
        var merchantA = IntegrationDb.MerchantA;
        var merchantB = IntegrationDb.MerchantB;
        var roleId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var adminConnection = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.ExecAsync(adminConnection,
            """
            DELETE access.PlatformAccessRoles WHERE PlatformAccessId IN (SELECT Id FROM access.PlatformAccess WHERE EmployeeAccountId IN (SELECT Id FROM acct.Accounts WHERE DisplayName IN (N'Access test', N'Agent test', N'Employee test')));
            DELETE access.PlatformAccess WHERE EmployeeAccountId IN (SELECT Id FROM acct.Accounts WHERE DisplayName IN (N'Access test', N'Agent test', N'Employee test'));
            DELETE access.AccessRoles WHERE MerchantAccessId IN (SELECT Id FROM access.MerchantAccess WHERE AccountId IN (SELECT Id FROM acct.Accounts WHERE DisplayName IN (N'Access test', N'Agent test', N'Employee test')));
            DELETE access.BranchAccess WHERE MerchantAccessId IN (SELECT Id FROM access.MerchantAccess WHERE AccountId IN (SELECT Id FROM acct.Accounts WHERE DisplayName IN (N'Access test', N'Agent test', N'Employee test')));
            DELETE access.MerchantAccess WHERE AccountId IN (SELECT Id FROM acct.Accounts WHERE DisplayName IN (N'Access test', N'Agent test', N'Employee test'));
            DELETE acct.Agents WHERE AccountId IN (SELECT Id FROM acct.Accounts WHERE DisplayName IN (N'Access test', N'Agent test', N'Employee test'));
            DELETE acct.Employees WHERE AccountId IN (SELECT Id FROM acct.Accounts WHERE DisplayName IN (N'Access test', N'Agent test', N'Employee test'));
            DELETE iam.Roles WHERE Code LIKE N'task2_%';
            DELETE merch.Originators WHERE Code LIKE N'task2_%';
            DELETE merch.Merchants WHERE Code LIKE N'task2_%';
            DELETE acct.Accounts WHERE DisplayName IN (N'Access test', N'Agent test', N'Employee test');
            """);
        await IntegrationDb.ExecAsync(adminConnection,
            """
            INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
            VALUES (@account, 1, N'Access test', 1, 0, @now, @now),
                   (@agent, 2, N'Agent test', 1, 0, @now, @now),
                   (@employee, 1, N'Employee test', 1, 0, @now, @now);
            INSERT acct.Employees (AccountId, EmployeeCode, DepartmentCode, Metadata, Id)
            VALUES (@employee, NULL, NULL, N'{}', @employee);
            INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version)
            VALUES (@access, @account, @merchantA, 1, 1, 1);
            """,
            ("@account", accountId), ("@agent", agentAccountId), ("@employee", employeeAccountId),
            ("@access", accessId), ("@merchantA", merchantA), ("@now", now));
        await IntegrationDb.ExecAsync(adminConnection,
            """
            INSERT merch.Merchants (Id, Code, Name, Note, Status, Country, Currency, EnabledChannels, CreatedAt, Metadata, Version, PaymentEnvironment, PaymentEnvironmentUpdatedAt)
            VALUES (@merchantA, @merchantCodeA, N'Task2 Merchant A', NULL, 1, N'TH', N'THB', N'card', @now, N'{}', 1, 1, @now),
                   (@merchantB, @merchantCodeB, N'Task2 Merchant B', NULL, 1, N'TH', N'THB', N'card', @now, N'{}', 1, 1, @now);
            """,
            ("@merchantA", merchantA), ("@merchantB", merchantB),
            ("@merchantCodeA", $"task2_{Guid.NewGuid():N}"[..24]), ("@merchantCodeB", $"task2_{Guid.NewGuid():N}"[..24]),
            ("@now", now));
        await IntegrationDb.ExecAsync(adminConnection,
            "INSERT iam.Roles (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId) VALUES (@role, @code, N'cross-merchant', NULL, NULL, 1, 1, 2, @merchant);",
            ("@role", roleId), ("@code", $"task2_{Guid.NewGuid():N}"[..24]), ("@merchant", merchantB));
        await IntegrationDb.ExecAsync(adminConnection,
            "INSERT merch.Originators (Id, MerchantId, Code, Name, Type, SaleCode, ApiClientId, Status, CreatedAt, UpdatedAt, Version) VALUES (@sale, @merchant, @code, N'cross-merchant sale', 2, NULL, NULL, 1, @now, @now, 1);",
            ("@sale", saleId), ("@merchant", merchantB), ("@code", $"task2_{Guid.NewGuid():N}"[..24]), ("@now", now));

        await AssertSqlRejectedAsync(() => IntegrationDb.ExecAsync(connection,
            "INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version) VALUES (@id, @account, @merchant, 1, 1, 1);",
            ("@id", Guid.NewGuid()), ("@account", accountId), ("@merchant", merchantA)));
        await AssertSqlRejectedAsync(() => IntegrationDb.ExecAsync(connection,
            "INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version) VALUES (@id, @account, @merchant, 99, 1, 1);",
            ("@id", Guid.NewGuid()), ("@account", Guid.NewGuid()), ("@merchant", merchantA)));
        await IntegrationDb.ExecAsync(connection,
            "INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version) VALUES (@id, @account, @merchant, 1, 2, 2);",
            ("@id", Guid.NewGuid()), ("@account", accountId), ("@merchant", merchantA));

        await AssertSqlRejectedAsync(() => IntegrationDb.ExecAsync(connection,
            "INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId) VALUES (@id, @access, @merchant, @role);",
            ("@id", Guid.NewGuid()), ("@access", accessId), ("@merchant", merchantB), ("@role", roleId)));
        await AssertSqlRejectedAsync(() => IntegrationDb.ExecAsync(connection,
            "INSERT access.BranchAccess (Id, MerchantAccessId, MerchantId, BranchId) VALUES (@id, @access, @merchant, @branch);",
            ("@id", Guid.NewGuid()), ("@access", accessId), ("@merchant", merchantB), ("@branch", branchId)));
        Assert.Equal(AuthorizationDecisionReason.MerchantMismatch,
            AccessEvaluator.ValidateReferenceMerchants(merchantA, [merchantB], [merchantA], saleMerchantId: merchantB).Reason);
        await AssertSqlRejectedAsync(() => IntegrationDb.ExecAsync(connection,
            "INSERT acct.Agents (AccountId, MerchantId, SaleId, Metadata, Id) VALUES (@account, @merchant, @sale, N'{}', @account);",
            ("@account", agentAccountId), ("@merchant", merchantA), ("@sale", saleId)));

        await AssertSqlRejectedAsync(() => IntegrationDb.ExecAsync(connection,
            "INSERT access.PlatformAccess (Id, EmployeeAccountId, Status, Version) VALUES (@id, @agent, 1, 1);",
            ("@id", Guid.NewGuid()), ("@agent", agentAccountId)));
        var platformAccessId = Guid.NewGuid();
        await IntegrationDb.ExecAsync(connection,
            "INSERT access.PlatformAccess (Id, EmployeeAccountId, Status, Version) VALUES (@id, @employee, 1, 1);",
            ("@id", platformAccessId), ("@employee", employeeAccountId));
        await AssertSqlRejectedAsync(() => IntegrationDb.ExecAsync(connection,
            "INSERT access.PlatformAccessRoles (Id, PlatformAccessId, RoleId, RoleScope) VALUES (@id, @access, @role, 2);",
            ("@id", Guid.NewGuid()), ("@access", platformAccessId), ("@role", roleId)));
        await IntegrationDb.ExecAsync(connection,
            "INSERT access.PlatformAccessRoles (Id, PlatformAccessId, RoleId, RoleScope) VALUES (@id, @access, @role, 1);",
            ("@id", Guid.NewGuid()), ("@access", platformAccessId), ("@role", roleId));

        await IntegrationDb.ExecAsync(adminConnection,
            "DELETE access.PlatformAccessRoles WHERE PlatformAccessId=@platform; DELETE access.PlatformAccess WHERE Id=@platform; DELETE acct.Employees WHERE AccountId=@employee; DELETE acct.Agents WHERE AccountId=@agent; DELETE iam.Roles WHERE Id=@role; DELETE merch.Originators WHERE Id=@sale; DELETE merch.Merchants WHERE Id IN (@merchantA, @merchantB); DELETE access.MerchantAccess WHERE AccountId=@account; DELETE acct.Accounts WHERE Id IN (@account, @agent, @employee);",
            ("@platform", platformAccessId), ("@account", accountId), ("@agent", agentAccountId), ("@employee", employeeAccountId), ("@role", roleId), ("@sale", saleId), ("@merchantA", merchantA), ("@merchantB", merchantB));
    }

    private static async Task AssertSqlRejectedAsync(Func<Task> operation)
    {
        await Assert.ThrowsAnyAsync<SqlException>(operation);
    }
}
