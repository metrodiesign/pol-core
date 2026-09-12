using Iam.Domain.Permissions;

namespace Iam.Application.Permissions;

/// <summary>The permission catalog (REQ-1.5/6.4), replacing the two duplicated
/// <c>Admins.Application.Permissions.PermissionCatalogResult</c> /
/// <c>Merchants.Application.Users.Permissions.PermissionCatalogResult</c>. Already filtered to one side's
/// <c>Scope</c> by the store — a console never sees the other side's vocabulary. <see cref="PermissionItem.Resource"/>
/// = the permission's group key (unchanged wire field name).</summary>
public sealed record PermissionCatalogResult(
    IReadOnlyList<PermissionGroupItem> Groups, IReadOnlyList<PermissionItem> Permissions);

public sealed record PermissionGroupItem(string Key, string Name, PermissionStatus Status);

public sealed record PermissionItem(string Key, string Name, string Resource, PermissionStatus Status);
