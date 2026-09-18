using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Integration.Tests;

namespace Hosts.Tests;

/// <summary>
/// An employee authenticated with the platform JWT has no <c>admin.Users</c> row: every admin-plane write that
/// still reads the legacy table must work from <c>acct.Accounts</c> + <c>access.PlatformAccess</c> alone.
/// </summary>
[Trait("Capability", "IdentityAccess")]
[Trait("Category", "Integration")]
public sealed class EmployeeJwtLegacyAdminPlaneTests
{
    private static readonly Guid PlatformAdminRoleId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Provisioning_a_merchant_works_for_a_jwt_employee_with_platform_access()
    {
        var accountId = Guid.CreateVersion7();
        var code = await PickAbsentAllowlistedCodeAsync();
        await SeedPlatformAdminAsync(accountId, "Provisioning employee");
        try
        {
            using var factory = new TokenFlowFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var login = await EmployeeTokenFlow.LoginAsync(client, factory.Services, accountId);

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/merchants");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
            request.Content = new StringContent($$"""
                {
                  "merchant": { "code": "{{code}}", "name": "JWT provisioned {{code}}", "country": "TH", "currency": "THB", "enabledChannels": ["card"] },
                  "pspConnections": [ { "psp": "2c2p", "enabledMethods": ["card"], "merchantId": "dummy-merchant-0001", "secrets": { "secretKey": "dummy-fixture-secret-0001" } } ]
                }
                """, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);

            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await CleanupAsync(accountId, code);
        }
    }

    [Fact]
    public async Task Merchant_payment_method_write_passes_the_authorization_lease_for_a_jwt_employee()
    {
        var accountId = Guid.CreateVersion7();
        var merchantId = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N")[..8];
        await SeedPlatformAdminAsync(accountId, "Lease employee");
        await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConn))
            await IntegrationDb.InsertMerchantAsync(seed, merchantId, $"lease{runTag}");
        try
        {
            using var factory = new TokenFlowFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var login = await EmployeeTokenFlow.LoginAsync(client, factory.Services, accountId);

            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/payments/merchants/{merchantId}/methods/card");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
            request.Headers.TryAddWithoutValidation("If-Match", "\"v0\"");
            request.Headers.Add("Idempotency-Key", $"lease-{runTag}");
            request.Content = JsonContent.Create(new { enabled = false });
            using var response = await client.SendAsync(request);

            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await CleanupAsync(accountId, $"lease{runTag}");
        }
    }

    [Fact]
    public async Task An_employee_cannot_suspend_their_own_account()
    {
        var accountId = Guid.CreateVersion7();
        await SeedPlatformAdminAsync(accountId, "Self suspend employee");
        try
        {
            using var factory = new TokenFlowFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var login = await EmployeeTokenFlow.LoginAsync(client, factory.Services, accountId);

            using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/accounts/{accountId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
            request.Headers.TryAddWithoutValidation("If-Match", "\"v0\"");
            request.Headers.Add("Idempotency-Key", $"self-suspend-{Guid.NewGuid():N}");
            request.Content = JsonContent.Create(new { displayName = "Self suspend employee", status = "Suspended" });
            using var response = await client.SendAsync(request);

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await CleanupAsync(accountId, null);
        }
    }

    // Merchant codes are a captive allowlist (Merchants.Domain.MerchantCode); provision whichever one this
    // database does not hold yet and delete it again afterwards.
    private static async Task<string> PickAbsentAllowlistedCodeAsync()
    {
        await using var db = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        foreach (var code in Merchants.Domain.MerchantCode.AllowList)
        {
            var exists = Convert.ToInt32(await IntegrationDb.ScalarAsync(db,
                "SELECT COUNT(*) FROM merch.Merchants WHERE Code=@code;", ("@code", code)));
            if (exists == 0)
                return code;
        }
        throw new InvalidOperationException("Every allowlisted merchant code already exists in the test database.");
    }

    private static async Task SeedPlatformAdminAsync(Guid accountId, string displayName)
    {
        await using var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(seed, """
            INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
            VALUES (@account, 1, @name, 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT acct.Employees (AccountId, EmployeeCode, DepartmentCode, Metadata)
            VALUES (@account, @employeeCode, N'OPS', N'{}');
            INSERT access.PlatformAccess (Id, EmployeeAccountId, Status, Version) VALUES (@access, @account, 1, 1);
            INSERT access.PlatformAccessRoles (Id, PlatformAccessId, RoleId, RoleScope) VALUES (@accessRole, @access, @role, 1);
            """, ("@account", accountId), ("@name", displayName), ("@access", Guid.CreateVersion7()),
            ("@accessRole", Guid.CreateVersion7()), ("@role", PlatformAdminRoleId),
            ("@employeeCode", $"JWT-{accountId.ToString("N")[..12]}"));
    }

    private static async Task CleanupAsync(Guid accountId, string? merchantCode)
    {
        await using var db = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(db, """
            DELETE FROM access.PlatformAccessRoles WHERE PlatformAccessId IN (SELECT Id FROM access.PlatformAccess WHERE EmployeeAccountId=@account);
            DELETE FROM access.PlatformAccess WHERE EmployeeAccountId=@account;
            DELETE FROM acct.Employees WHERE AccountId=@account;
            DELETE FROM acct.Accounts WHERE Id=@account;
            """, ("@account", accountId));
        await IntegrationDb.ExecAsync(db, "DELETE FROM admin.ProvisioningOperations WHERE CallerAdminId=@account;", ("@account", accountId));
        if (merchantCode is not null)
            await IntegrationDb.ExecAsync(db, """
                DECLARE @merchant uniqueidentifier = (SELECT Id FROM merch.Merchants WHERE Code=@code);
                DELETE FROM txn.MerchantProviderAccountMethodOptions WHERE MerchantId=@merchant;
                DELETE FROM txn.MerchantProviderAccountMethods WHERE MerchantId=@merchant;
                DELETE FROM txn.MerchantPaymentMethods WHERE MerchantId=@merchant;
                DELETE FROM txn.RoutingRules WHERE MerchantId=@merchant;
                DELETE FROM txn.PspConnections WHERE MerchantId=@merchant;
                DELETE FROM merch.VaultSecrets WHERE MerchantId=@merchant;
                DELETE FROM merch.ProvisioningAudits WHERE MerchantId=@merchant;
                DELETE FROM merch.Merchants WHERE Id=@merchant;
                """, ("@code", merchantCode));
    }
}
