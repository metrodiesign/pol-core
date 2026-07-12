namespace Iam.Domain.Permissions;

/// <summary>A catalog permission (REQ-1.1). Reference/catalog data seeded by migration; <see cref="Key"/> is the
/// natural primary key and the FK target of role grants (REQ-2.6). <see cref="GroupKey"/> references a
/// <see cref="PermissionGroup"/> and is the <c>resource</c> the frontend renders.</summary>
public sealed class Permission
{
    public string Key { get; private set; } = default!;
    public string GroupKey { get; private set; } = default!;
    public string LabelTh { get; private set; } = default!;
    public int SortOrder { get; private set; }

    private Permission() { }

    public Permission(string key, string groupKey, string labelTh, int sortOrder)
    {
        Key = key;
        GroupKey = groupKey;
        LabelTh = labelTh;
        SortOrder = sortOrder;
    }
}
