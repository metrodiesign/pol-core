extern alias ApiHost;

using System.Reflection;
using NetArchTest.Rules;

namespace Hosts.Tests;

/// <summary>
/// The scoped-admin app-layer floor (REQ-7.2): admin cross-merchant reads of a merchant-scoped business table must
/// go through the single <c>IAdminQuery</c> seam, which applies the <c>IAdminScope</c> filter. pol_admin
/// bypasses RLS at the DB, so a handler that sent <c>GetMerchantQuery</c> directly would read every merchant
/// unfiltered. This pins that <c>AdminQuery</c> is the ONLY type in the Api host that depends on
/// <c>GetMerchantQuery</c> — any new bypass fails CI.
/// </summary>
public sealed class AdminSeamArchitectureTests
{
    private static readonly Assembly ApiAssembly = typeof(ApiHost::Api.AdminQuery).Assembly;

    [Fact]
    public void Only_AdminQuery_may_send_the_cross_merchant_GetMerchantQuery_read()
    {
        var result = Types.InAssembly(ApiAssembly)
            // Exclude the Mediator source-generated registration plumbing — it references EVERY message type
            // (including GetMerchantQuery) by design; it is not an admin handler bypassing the seam.
            .That().DoNotResideInNamespace("Mediator")
            .And().DoNotHaveName("AdminQuery")
            .Should().NotHaveDependencyOn("Merchants.Application.GetMerchant.GetMerchantQuery")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Only AdminQuery may send GetMerchantQuery (REQ-7.2 scoped-admin floor). Offenders: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
