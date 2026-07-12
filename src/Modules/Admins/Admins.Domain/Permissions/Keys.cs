namespace Admins.Domain.Permissions;

/// <summary>
/// The canonical Admin permission vocabulary (REQ-1, REQ-11). Code owns the KEY set + each key's group; the
/// SeedData migration seeds admin.Keys / AdminPermissionGroups (with Thai labels +
/// sort order) FROM this same vocabulary, and an integration test asserts the seeded rows equal <see cref="All"/>
/// so code and DB never drift. The inline boot parity guard checks every <c>RequirePermission</c> gate key against
/// <see cref="AllKeys"/> (REQ-11) WITHOUT touching the database. A new feature adds its permission here + a seed
/// migration row (and a gate that uses it) — the catalog is feature-sourced and open-ended.
/// </summary>
public static class Keys
{
    // Resource group keys (the buckets the console UI shows).
    public const string GroupTxn = "txn";
    public const string GroupMerchant = "merchant";
    public const string GroupFinance = "finance";
    public const string GroupUser = "user";
    public const string GroupSystem = "system";
    // The cross-catalog merchant-user-approval group (producer-google-sso REQ-18.1) — the single intentional coupling
    // between the Admin and Merchants RBAC systems: the Admin who approves a merchant user is gated by a real Admin permission.
    public const string GroupMerchantUser = "merchants.users";

    // Permission keys — stable strings, never renamed once shipped (CODING_STANDARDS).
    public const string TxnView = "txn.view";
    public const string TxnRefund = "txn.refund";
    public const string TxnExport = "txn.export";
    public const string MerchantView = "merchant.view";
    public const string MerchantManage = "merchant.manage";
    public const string InvoiceView = "invoice.view";
    public const string InvoiceManage = "invoice.manage";
    public const string SettlementRun = "settlement.run";
    public const string UserView = "user.view";
    public const string UserManage = "user.manage";
    public const string UserRoles = "user.roles";
    public const string AuditView = "audit.view";
    public const string SettingsManage = "settings.manage";
    public const string ApiKeyManage = "apikey.manage";
    // Merchant-user-approval keys (producer-google-sso REQ-18.1, renamed REQ-2.6): an Admin holding these may
    // approve/reject a merchant user.
    public const string MerchantUserApprove = "merchants.users.approve";
    public const string MerchantUserReject = "merchants.users.reject";

    /// <summary>Every (key, group) pair in display order. The migration seed mirrors this exactly.</summary>
    public static readonly IReadOnlyList<(string Key, string GroupKey)> All =
    [
        (TxnView, GroupTxn), (TxnRefund, GroupTxn), (TxnExport, GroupTxn),
        (MerchantView, GroupMerchant), (MerchantManage, GroupMerchant),
        (InvoiceView, GroupFinance), (InvoiceManage, GroupFinance), (SettlementRun, GroupFinance),
        (UserView, GroupUser), (UserManage, GroupUser), (UserRoles, GroupUser),
        (AuditView, GroupSystem), (SettingsManage, GroupSystem), (ApiKeyManage, GroupSystem),
        (MerchantUserApprove, GroupMerchantUser), (MerchantUserReject, GroupMerchantUser),
    ];

    /// <summary>All valid permission keys — the parity reference (REQ-11) and the role-grant catalog (REQ-3.3).</summary>
    public static readonly IReadOnlySet<string> AllKeys = All.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>The six resource group keys in display order.</summary>
    public static readonly IReadOnlyList<string> GroupKeys =
        [GroupTxn, GroupMerchant, GroupFinance, GroupUser, GroupSystem, GroupMerchantUser];
}
