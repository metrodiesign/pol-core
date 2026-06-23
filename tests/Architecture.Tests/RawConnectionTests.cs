using System.Reflection;

using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// The RLS floor (PLAN #3) trusts that <c>SESSION_CONTEXT('TenantId')</c> is set by the
/// <see cref="BuildingBlocks.Infrastructure.Persistence.SessionContextConnectionInterceptor"/> at
/// connection open. A hand-rolled <c>Microsoft.Data.SqlClient.SqlConnection</c> would open a
/// connection the interceptor never sees — no tenant context, RLS silently wide open under the bypass
/// role or just broken. This test bans that type from production infrastructure so every connection
/// goes through EF + the interceptor. (Tests legitimately use SqlConnection to drive RLS directly;
/// they are not in scope here.)
/// </summary>
public class RawConnectionTests
{
    private static readonly Assembly[] ProductionInfrastructure =
    [
        typeof(BuildingBlocks.Infrastructure.Persistence.ProducerDbContext).Assembly,
        typeof(global::Products.Infrastructure.ProductsModuleRegistration).Assembly,
        typeof(global::Cart.Infrastructure.CartModuleRegistration).Assembly,
        typeof(global::Checkout.Infrastructure.CheckoutModuleRegistration).Assembly,
        typeof(global::Orders.Infrastructure.OrdersModuleRegistration).Assembly,
        typeof(global::Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
        typeof(global::Tenant.Infrastructure.TenantModuleRegistration).Assembly,
    ];

    [Fact]
    public void Production_infrastructure_never_opens_a_raw_SqlConnection()
    {
        // Prefix match: catches SqlConnection (+ SqlConnectionStringBuilder) but NOT SqlException,
        // which EfIdempotencyStore legitimately inspects to detect a unique-violation.
        var result = Types.InAssemblies(ProductionInfrastructure)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.Data.SqlClient.SqlConnection")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Raw SqlConnection bypasses the RLS SESSION_CONTEXT interceptor. Offenders: " +
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }
}
