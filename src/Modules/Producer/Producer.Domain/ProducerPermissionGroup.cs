namespace Producer.Domain;

/// <summary>A resource bucket that groups permissions in the producer console UI (REQ-15.1). Reference/catalog data
/// seeded by migration; <see cref="Key"/> is the natural primary key.</summary>
// ponytail: DUPLICATE of Admin.Domain.AdminPermissionGroup — deliberate debt, do not refactor into a shared base.
public sealed class ProducerPermissionGroup
{
    public string Key { get; private set; } = default!;
    public string LabelTh { get; private set; } = default!;
    public int SortOrder { get; private set; }

    private ProducerPermissionGroup() { }

    public ProducerPermissionGroup(string key, string labelTh, int sortOrder)
    {
        Key = key;
        LabelTh = labelTh;
        SortOrder = sortOrder;
    }
}
