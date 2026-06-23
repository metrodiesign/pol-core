extern alias ApiHost;

using System.Reflection;
using NetArchTest.Rules;

namespace Hosts.Tests;

/// <summary>
/// The scoped-admin app-layer floor (REQ-7.2): admin cross-tenant reads of a tenant-scoped business table must
/// go through the single <c>IAdminQuery</c> seam, which applies the <c>IAdminScope</c> filter. pol_admin
/// bypasses RLS at the DB, so a handler that sent <c>GetTenantQuery</c> directly would read every tenant
/// unfiltered. This pins that <c>AdminQuery</c> is the ONLY type in the Api host that depends on
/// <c>GetTenantQuery</c> — any new bypass fails CI.
/// </summary>
public sealed class AdminSeamArchitectureTests
{
    private static readonly Assembly ApiAssembly = typeof(ApiHost::Api.AdminQuery).Assembly;

    [Fact]
    public void Only_AdminQuery_may_send_the_cross_tenant_GetTenantQuery_read()
    {
        var result = Types.InAssembly(ApiAssembly)
            // Exclude the Mediator source-generated registration plumbing — it references EVERY message type
            // (including GetTenantQuery) by design; it is not an admin handler bypassing the seam.
            .That().DoNotResideInNamespace("Mediator")
            .And().DoNotHaveName("AdminQuery")
            .Should().NotHaveDependencyOn("Tenant.Application.GetTenant.GetTenantQuery")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Only AdminQuery may send GetTenantQuery (REQ-7.2 scoped-admin floor). Offenders: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
