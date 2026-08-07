namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The rf1 multi-schema layout (design.md "Schema map"). Every entity configuration must call
/// <c>ToTable(name, schema: SchemaNames.X)</c> explicitly — there is no <c>HasDefaultSchema</c>
/// fallback, so an entity that forgets its schema fails the Architecture.Tests guard instead of
/// landing in <c>dbo</c> silently.
/// </summary>
public static class SchemaNames
{
    /// <summary>Commerce: Carts, CartItems and Orders.</summary>
    public const string Shop = "shop";

    /// <summary>Payment (interim): PaymentSessions, PspConnections, OutboxMessages, IdempotencyRecords.</summary>
    public const string Txn = "txn";

    /// <summary>
    /// Control plane: PlatformUsers, PlatformMerchantAccess, RBAC catalog.
    /// Deliberately stays singular even though the <c>Admins.*</c> module projects are plural
    /// (hierarchical-naming L3/L7, design.md §1) — schemas are SQL namespaces, singular is the SQL
    /// convention, and rf1 already locked this schema name. Do not "fix" it to match the module name.
    /// </summary>
    public const string Admin = "admin";

    /// <summary>Merchant + merchant-user + vault: Merchants, MerchantUsers, RBAC catalog, vault.</summary>
    public const string Merch = "merch";

    /// <summary>Central IAM catalog (rf2): Permissions, PermissionGroups, Roles, RolePermissions — the single
    /// vocabulary replacing the duplicated admin/merch RBAC catalogs. No RLS predicate (design.md REQ-9.2);
    /// per-merchant visibility on <c>Roles</c>/<c>RolePermissions</c> is an app-layer floor.</summary>
    public const string Iam = "iam";

    /// <summary>Framework-owned; the ONE named exception to the schema guard (REQ-1.4): DataProtectionKeys.</summary>
    public const string Dbo = "dbo";

    /// <summary>Config/reference data: Positions, Offices, Levels, Divisions — owned by the four standalone
    /// reference modules Divisions/Levels/Offices/Positions (masterdata-split, 2026-07-19; they replace the
    /// retired single master-data module that first occupied this schema). rf3 will add
    /// Provider/RoutingRule/GatewayConfig/FeeStructure to this same schema.
    /// Control-plane, no RLS predicate (same rule as <see cref="Iam"/>).</summary>
    public const string Cfg = "cfg";
}
