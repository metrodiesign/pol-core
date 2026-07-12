namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The rf1 multi-schema layout (design.md "Schema map"). Every entity configuration must call
/// <c>ToTable(name, schema: SchemaNames.X)</c> explicitly — there is no <c>HasDefaultSchema</c>
/// fallback, so an entity that forgets its schema fails the Architecture.Tests guard instead of
/// landing in <c>dbo</c> silently.
/// </summary>
public static class SchemaNames
{
    /// <summary>Funnel: Products, Carts, CartItems, CheckoutSessions, Orders.</summary>
    public const string Shop = "shop";

    /// <summary>Payment (interim): PaymentSessions, PspConnections, OutboxMessages, IdempotencyRecords.</summary>
    public const string Txn = "txn";

    /// <summary>Control plane: PlatformUsers, PlatformMerchantAccess, RBAC catalog, master data.</summary>
    public const string Admin = "admin";

    /// <summary>Merchant + merchant-user + vault: Merchants, MerchantUsers, RBAC catalog, vault.</summary>
    public const string Merch = "merch";

    /// <summary>RLS functions/procs only — no tables live here.</summary>
    public const string Sec = "sec";

    /// <summary>Framework-owned; the ONE named exception to the schema guard (REQ-1.4): DataProtectionKeys.</summary>
    public const string Dbo = "dbo";
}
