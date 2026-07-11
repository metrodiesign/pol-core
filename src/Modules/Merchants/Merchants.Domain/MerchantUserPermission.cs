namespace Merchants.Domain;

/// <summary>A catalog permission. Reference/catalog data seeded by migration; <see cref="Key"/> is the
/// natural primary key and the FK target of role grants. <see cref="GroupKey"/> references a
/// <see cref="MerchantUserPermissionGroup"/> and is the <c>resource</c> the frontend renders.</summary>
// ponytail: DUPLICATE of Admin.Domain.AdminPermission — deliberate debt, do not refactor into a shared base.
public sealed class MerchantUserPermission
{
    public string Key { get; private set; } = default!;
    public string GroupKey { get; private set; } = default!;
    public string LabelTh { get; private set; } = default!;
    public int SortOrder { get; private set; }

    private MerchantUserPermission() { }

    public MerchantUserPermission(string key, string groupKey, string labelTh, int sortOrder)
    {
        Key = key;
        GroupKey = groupKey;
        LabelTh = labelTh;
        SortOrder = sortOrder;
    }
}
