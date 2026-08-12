namespace Iam.Domain.Permissions;

/// <summary>Which console a permission group/role belongs to (REQ-1.1/2.1). Platform = admin console;
/// Merchant = merchant-user console. Assign/grant across the wrong side is fail-closed by construction
/// (REQ-3.4/3.5/6.6) rather than by convention, closing the cross-side grant hole the two duplicated
/// catalogs had no way to detect.</summary>
public enum Scope
{
    Platform = 1,
    Merchant = 2,
}

/// <summary>
/// The single canonical permission vocabulary (REQ-1.2) replacing the two duplicated catalogs
/// (<c>Admins.Domain.Permissions.Keys</c> + <c>Merchants.Domain.Users.Permissions.Keys</c>). Code owns the
/// KEY set, each key's group, and each group's <see cref="Scope"/>; the SeedData migration seeds
/// <c>iam.Permissions</c>/<c>iam.PermissionGroups</c> FROM this same vocabulary, and an integration test
/// asserts the seeded rows equal <see cref="All"/> so code and DB never drift. The boot parity guard checks
/// every gated key against <see cref="AllKeys"/> AND its <see cref="KeySide"/> against the endpoint's policy
/// (REQ-5.1/5.4) without touching the database. 26 keys / 7 groups — the old admin-only
/// <c>invoice.view</c>/<c>invoice.manage</c>/<c>settlement.run</c> (group <c>finance</c>) are dropped
/// (REQ-2.2: ungated and colliding with the settlement/billing Non-Goals). Policy groups/keys were retired
/// with their API/data surfaces. <c>product.create</c>/<c>product.update</c> (group <c>catalog</c>) were retired
/// once orphaned — <c>POST /api/v1/products</c> was removed (commit 152b692, the catalogue is read-only over
/// HTTP for good) and no endpoint gated on them anymore; the now-empty <c>catalog</c> group was dropped too.
/// registration-attempt-history (REQ-4.1) added <c>merchants.users.view</c> under the existing
/// <c>merchants.users</c> group for the admin registration-history endpoint.
/// </summary>
public static class Keys
{
    // Group keys — stable strings, never renamed once shipped (CODING_STANDARDS).
    public const string GroupTxn = "txn";
    public const string GroupMerchant = "merchant";
    public const string GroupUser = "user";
    public const string GroupSystem = "system";
    public const string GroupMerchantUsers = "merchants.users";
    public const string GroupPayment = "payment";
    public const string GroupRoles = "roles";

    // Permission keys — stable strings, carried over LITERAL from the two prior catalogs (REQ-1.3).
    public const string TxnView = "txn.view";
    public const string TxnRefund = "txn.refund";
    public const string TxnExport = "txn.export";
    public const string TxnManage = "txn.manage";
    public const string MerchantView = "merchant.view";
    public const string MerchantManage = "merchant.manage";
    public const string UserView = "user.view";
    public const string UserManage = "user.manage";
    public const string UserRoles = "user.roles";
    public const string AuditView = "audit.view";
    public const string SettingsManage = "settings.manage";
    public const string ApiKeyManage = "apikey.manage";
    public const string MerchantUserApprove = "merchants.users.approve";
    public const string MerchantUserReject = "merchants.users.reject";
    public const string MerchantUserView = "merchants.users.view";
    public const string MerchantUserManage = "merchants.users.manage";
    public const string MerchantRolesView = "merchants.roles.view";
    public const string MerchantRolesManage = "merchants.roles.manage";
    public const string PaymentView = "payment.view";
    public const string PaymentCreate = "payment.create";
    public const string PaymentRedirect = "payment.redirect";
    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string RolesView = "roles.view";
    public const string RolesManage = "roles.manage";
    // Distinct C# member from UserRoles above: same-looking literal families ("user.roles" vs "users.roles")
    // that used to live in two separate catalogs, one per console — now side by side in one class, so the
    // names must not collide either (REQ-10.4 pins both members AND both literals against a silent swap).
    public const string UsersRoles = "users.roles";
    /// <summary>Every group's <see cref="Scope"/> (REQ-2.1) — the single source both <see cref="KeySide"/> and
    /// the SeedData migration derive from.</summary>
    public static readonly IReadOnlyDictionary<string, Scope> GroupScope = new Dictionary<string, Scope>(StringComparer.Ordinal)
    {
        [GroupTxn] = Scope.Platform,
        [GroupMerchant] = Scope.Platform,
        [GroupUser] = Scope.Platform,
        [GroupSystem] = Scope.Platform,
        [GroupMerchantUsers] = Scope.Platform,
        [GroupPayment] = Scope.Merchant,
        [GroupRoles] = Scope.Merchant,
    };

    /// <summary>The seven group keys in display order.</summary>
    public static readonly IReadOnlyList<string> GroupKeys =
    [
        GroupTxn, GroupMerchant, GroupUser, GroupSystem, GroupMerchantUsers, GroupPayment, GroupRoles,
    ];

    /// <summary>Every (key, group) pair in display order. The migration seed mirrors this exactly.</summary>
    public static readonly IReadOnlyList<(string Key, string GroupKey)> All =
    [
        (TxnView, GroupTxn), (TxnRefund, GroupTxn), (TxnExport, GroupTxn), (TxnManage, GroupTxn),
        (MerchantView, GroupMerchant), (MerchantManage, GroupMerchant),
        (UserView, GroupUser), (UserManage, GroupUser), (UserRoles, GroupUser),
        (AuditView, GroupSystem), (SettingsManage, GroupSystem), (ApiKeyManage, GroupSystem),
        (MerchantUserApprove, GroupMerchantUsers), (MerchantUserReject, GroupMerchantUsers),
        (MerchantUserView, GroupMerchantUsers), (MerchantUserManage, GroupMerchantUsers),
        (MerchantRolesView, GroupMerchantUsers), (MerchantRolesManage, GroupMerchantUsers),
        (PaymentView, GroupPayment), (PaymentCreate, GroupPayment), (PaymentRedirect, GroupPayment),
        (UsersView, GroupRoles), (UsersManage, GroupRoles),
        (RolesView, GroupRoles), (RolesManage, GroupRoles), (UsersRoles, GroupRoles),
    ];

    /// <summary>All valid permission keys — the parity reference (REQ-5.1) and the role-grant catalog (REQ-2.6).</summary>
    public static readonly IReadOnlySet<string> AllKeys = All.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every key's <see cref="Scope"/>, derived from its group (REQ-5.4/6.4/6.6) — the side-awareness
    /// the two separate catalogs got "for free" from being physically apart, and the merged catalog must now
    /// compute explicitly.</summary>
    public static readonly IReadOnlyDictionary<string, Scope> KeySide =
        All.ToDictionary(x => x.Key, x => GroupScope[x.GroupKey], StringComparer.Ordinal);
}
