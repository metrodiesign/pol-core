namespace Iam.Domain.Permissions;

/// <summary>A resource bucket that groups permissions in the console UI (REQ-1.1). Reference/catalog data seeded by
/// migration; <see cref="Key"/> is the natural primary key. <see cref="Scope"/> is which console the group (and
/// every key inside it) belongs to (REQ-2.1) — the side-awareness the parity guard and grant validation key off.</summary>
public sealed class PermissionGroup
{
    public string Key { get; private set; } = default!;
    public Scope Scope { get; private set; }
    public string Name { get; private set; } = default!;
    public PermissionStatus Status { get; private set; }
    public int SortOrder { get; private set; }

    private PermissionGroup() { }

    public PermissionGroup(string key, Scope scope, string name, int sortOrder,
        PermissionStatus status = PermissionStatus.Active)
    {
        Key = key;
        Scope = scope;
        Name = name;
        SortOrder = sortOrder;
        Status = status;
    }

    public void Activate() => Status = PermissionStatus.Active;
    public void Deactivate() => Status = PermissionStatus.Inactive;
}
