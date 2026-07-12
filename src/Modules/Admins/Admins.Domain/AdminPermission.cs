namespace Admins.Domain;

/// <summary>A catalog permission (REQ-1.1). Reference/catalog data seeded by migration; <see cref="Key"/> is the
/// natural primary key and the FK target of role grants (REQ-3.2). <see cref="GroupKey"/> references an
/// <see cref="AdminPermissionGroup"/> and is the <c>resource</c> the frontend renders.</summary>
public sealed class AdminPermission
{
    public string Key { get; private set; } = default!;
    public string GroupKey { get; private set; } = default!;
    public string LabelTh { get; private set; } = default!;
    public int SortOrder { get; private set; }

    private AdminPermission() { }

    public AdminPermission(string key, string groupKey, string labelTh, int sortOrder)
    {
        Key = key;
        GroupKey = groupKey;
        LabelTh = labelTh;
        SortOrder = sortOrder;
    }
}
