namespace Producer.Domain;

/// <summary>
/// The canonical Producer permission vocabulary (REQ-15, REQ-16). Code owns the KEY set + each key's group; the
/// AddProducerRoleRbacTables migration seeds producer.ProducerPermissions / ProducerPermissionGroups (with Thai
/// labels + sort order) FROM this same vocabulary, and an integration test asserts the seeded rows equal
/// <see cref="AllKeys"/> so code and DB never drift. The boot parity guard checks every
/// <c>RequireProducerPermission</c> gate key against <see cref="AllKeys"/> (REQ-15.5) WITHOUT touching the
/// database. A new feature adds its permission here + a seed migration row (and a gate that uses it) — the catalog
/// is feature-sourced and open-ended.
/// </summary>
// ponytail: DUPLICATE of Admin.Domain.AdminPermissions — deliberate debt, do not refactor into a shared base.
public static class ProducerPermissions
{
    // Resource group keys (the buckets the producer console UI shows).
    public const string GroupCatalog = "catalog";
    public const string GroupPayment = "payment";
    public const string GroupRoles = "roles";

    // Permission keys — stable strings, never renamed once shipped (CODING_STANDARDS).
    public const string ProductCreate = "product.create";
    public const string ProductUpdate = "product.update";
    public const string PaymentCreate = "payment.create";
    public const string PaymentRedirect = "payment.redirect";
    public const string RolesView = "producer.roles.view";
    public const string RolesManage = "producer.roles.manage";
    public const string UserRoles = "producer.user.roles";

    /// <summary>Every (key, group) pair in display order. The migration seed mirrors this exactly.</summary>
    public static readonly IReadOnlyList<(string Key, string GroupKey)> All =
    [
        (ProductCreate, GroupCatalog), (ProductUpdate, GroupCatalog),
        (PaymentCreate, GroupPayment), (PaymentRedirect, GroupPayment),
        (RolesView, GroupRoles), (RolesManage, GroupRoles), (UserRoles, GroupRoles),
    ];

    /// <summary>All valid permission keys — the parity reference (REQ-15.5) and the role-grant catalog (REQ-16.2).</summary>
    public static readonly IReadOnlySet<string> AllKeys = All.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>The three resource group keys in display order.</summary>
    public static readonly IReadOnlyList<string> GroupKeys =
        [GroupCatalog, GroupPayment, GroupRoles];
}
