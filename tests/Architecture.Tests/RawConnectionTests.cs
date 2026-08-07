using System.Reflection;

using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// The tenant floor (task 8, "1 principal") lives entirely in EF: the read floor is each runtime context's
/// own query filter (<c>MerchantId==CurrentMerchant</c>) and the write floor is <c>IWriteAuthorizer</c>. A
/// hand-rolled <c>Microsoft.Data.SqlClient.SqlConnection</c> would bypass BOTH — no query filter, no write
/// check. This test bans that type from production infrastructure so every connection goes through EF.
/// (Tests legitimately use SqlConnection directly; they are not in scope here. <c>Persistence.Provisioning</c>
/// is also excluded — its shared-connection UoW legitimately opens one raw connection per attempt, task 7.)
/// <para>
/// rls-to-query-filter REQ-11.3 (task 3): coverage extended to Admins/Iam Infrastructure plus the four
/// reference-module Infrastructure assemblies (masterdata-split — the requirement originally named the
/// retired master-data module) and the three new runtime Persistence assemblies. The exact
/// allowlist exception for the provisioning-integration UoW's shared-connection requirement (design.md
/// section "Assembly split") is task 7's, once that assembly exists.
/// </para>
/// </summary>
public class RawConnectionTests
{
    private static readonly Assembly[] ProductionInfrastructure =
    [
        typeof(BuildingBlocks.Infrastructure.Persistence.PolDbContext).Assembly,
        typeof(global::Products.Infrastructure.ProductsModuleRegistration).Assembly,
        typeof(global::Carts.Infrastructure.CartModuleRegistration).Assembly,
        typeof(global::Orders.Infrastructure.OrdersModuleRegistration).Assembly,
        typeof(global::Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
        typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
        typeof(global::Admins.Infrastructure.AdminModuleRegistration).Assembly,
        typeof(global::Iam.Infrastructure.IamModuleRegistration).Assembly,
        typeof(global::Divisions.Infrastructure.DivisionsModuleRegistration).Assembly,
        typeof(global::Levels.Infrastructure.LevelsModuleRegistration).Assembly,
        typeof(global::Offices.Infrastructure.OfficesModuleRegistration).Assembly,
        typeof(global::Positions.Infrastructure.PositionsModuleRegistration).Assembly,
        typeof(Persistence.ControlPlane.ControlPlaneDbContext).Assembly,
        typeof(Persistence.MerchantUsers.MerchantUserDbContext).Assembly,
        typeof(Persistence.MerchantRuntime.MerchantRuntimeDbContext).Assembly,
    ];

    [Fact]
    public void Production_infrastructure_never_opens_a_raw_SqlConnection()
    {
        // Prefix match: catches SqlConnection (+ SqlConnectionStringBuilder) but NOT SqlException,
        // which EfIdempotencyStore legitimately inspects to detect a unique-violation.
        //
        // SpDocumentGateway (products-sp-gateway REQ-5.1) is the one exemption, and it is exempt for the
        // reason the rule exists rather than in spite of it: it never touches the app database. It calls two
        // stored procedures in the upstream insurance catalogues, which hold no merchant-scoped rows, are in
        // no DbContext model, and are reached through EXECUTE on that procedure — there is no query filter or
        // write authorizer to bypass. EF cannot express the call either (two result sets, no mapped entity).
        // Anything else in these assemblies reaching for SqlConnection is still the bug this test was
        // written to catch.
        var result = Types.InAssemblies(ProductionInfrastructure)
            .That()
            .DoNotHaveName("SpDocumentGateway")
            .ShouldNot()
            .HaveDependencyOn("Microsoft.Data.SqlClient.SqlConnection")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Raw SqlConnection bypasses the RLS SESSION_CONTEXT interceptor. Offenders: " +
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }
}
