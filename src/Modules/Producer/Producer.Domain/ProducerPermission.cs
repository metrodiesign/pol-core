namespace Producer.Domain;

/// <summary>A catalog permission (REQ-15.1). Reference/catalog data seeded by migration; <see cref="Key"/> is the
/// natural primary key and the FK target of role grants (REQ-16.2). <see cref="GroupKey"/> references a
/// <see cref="ProducerPermissionGroup"/> and is the <c>resource</c> the frontend renders.</summary>
// ponytail: DUPLICATE of Admin.Domain.AdminPermission — deliberate debt, do not refactor into a shared base.
public sealed class ProducerPermission
{
    public string Key { get; private set; } = default!;
    public string GroupKey { get; private set; } = default!;
    public string LabelTh { get; private set; } = default!;
    public int SortOrder { get; private set; }

    private ProducerPermission() { }

    public ProducerPermission(string key, string groupKey, string labelTh, int sortOrder)
    {
        Key = key;
        GroupKey = groupKey;
        LabelTh = labelTh;
        SortOrder = sortOrder;
    }
}
