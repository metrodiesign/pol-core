namespace Admins.Domain.Permissions;

/// <summary>A resource bucket that groups permissions in the console UI (REQ-1.1). Reference/catalog data seeded by
/// migration; <see cref="Key"/> is the natural primary key.</summary>
public sealed class PermissionGroup
{
    public string Key { get; private set; } = default!;
    public string LabelTh { get; private set; } = default!;
    public int SortOrder { get; private set; }

    private PermissionGroup() { }

    public PermissionGroup(string key, string labelTh, int sortOrder)
    {
        Key = key;
        LabelTh = labelTh;
        SortOrder = sortOrder;
    }
}
