namespace Admins.Application.Permissions;

/// <summary>The permission catalog (REQ-1.5). <see cref="PermissionItem.Resource"/> = the permission's group key.</summary>
public sealed record PermissionCatalogResult(
    IReadOnlyList<PermissionGroupItem> Groups, IReadOnlyList<PermissionItem> Permissions);

public sealed record PermissionGroupItem(string Key, string LabelTh);

public sealed record PermissionItem(string Key, string LabelTh, string Resource);
