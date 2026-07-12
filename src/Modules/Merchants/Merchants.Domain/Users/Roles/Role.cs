using System.Text.RegularExpressions;
using SharedKernel;

namespace Merchants.Domain.Users.Roles;

/// <summary>
/// A named, admin-managed permission set for merchant users (REQ-16 lineage). Control-plane. <see cref="Code"/> is
/// the stable, immutable identity; granted permissions are always a subset of the catalog. An Inactive
/// role contributes nothing to a merchant-user's effective permissions. The seed <see cref="MerchantOwnerCode"/>
/// role grants every merchant-user key as the deliberate recovery anchor and cannot be deactivated or deleted;
/// <see cref="MerchantMemberCode"/> is the ordinary default approval choice (product/payment only).
/// </summary>
// ponytail: DUPLICATE of Admins.Domain.AdminRole — deliberate debt, do not refactor into a shared base.
public sealed class Role : AggregateRoot<Guid>
{
    /// <summary>The seed role granting ALL merchant-user keys; the deliberate anchor that may not be deactivated or
    /// deleted.</summary>
    public const string MerchantOwnerCode = "merchant_owner";

    /// <summary>The ordinary default approval role (product/payment only, no role-management) — NOT an anchor.</summary>
    public const string MerchantMemberCode = "merchant_member";

    // Code lands in route paths (GET /merchants/users/roles/{code}); constrain to a URL-safe slug so it can never carry
    // '/', '?', '#', '%' etc. All seeded codes are lowercase snake_case.
    private static readonly Regex CodePattern = new("^[a-z0-9_]+$");

    private readonly List<RolePermission> _permissions = [];

    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public string? Color { get; private set; }
    public RoleStatus Status { get; private set; }

    /// <summary>The granted permission rows (EF navigation, backed by <c>_permissions</c>).</summary>
    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    /// <summary>The granted permission keys.</summary>
    public IReadOnlyCollection<string> PermissionKeys => [.. _permissions.Select(p => p.PermissionKey)];

    public bool IsMerchantOwnerSeed => string.Equals(Code, MerchantOwnerCode, StringComparison.Ordinal);

    private Role() { }

    private Role(Guid id, string code, string name, string? description, string? color, RoleStatus status)
        : base(id)
    {
        Code = code;
        Name = name;
        Description = description;
        Color = color;
        Status = status;
    }

    /// <summary>Creates a role with a validated permission subset. <paramref name="catalogKeys"/> is the live
    /// catalog vocabulary; any key outside it is rejected.</summary>
    public static Role Create(string code, string name, string? description, string? color,
        RoleStatus status, IEnumerable<string> permissionKeys, IReadOnlySet<string> catalogKeys)
    {
        var role = new Role(Guid.NewGuid(), NormalizeCode(code), NormalizeName(name),
            Trim(description), Trim(color), status);
        role.SetPermissions(permissionKeys, catalogKeys);
        return role;
    }

    public void Rename(string name) => Name = NormalizeName(name);

    public void SetDescription(string? description) => Description = Trim(description);

    public void SetColor(string? color) => Color = Trim(color);

    public void Activate() => Status = RoleStatus.Active;

    /// <summary>Deactivates the role. The <see cref="MerchantOwnerCode"/> seed is the recovery anchor and may never
    /// be deactivated.</summary>
    public void Deactivate()
    {
        if (IsMerchantOwnerSeed)
            throw new InvalidOperationException("The merchant_owner role cannot be deactivated.");
        Status = RoleStatus.Inactive;
    }

    /// <summary>Guards deletion: the <see cref="MerchantOwnerCode"/> anchor may never be deleted. The
    /// "still has ≥1 assignment" 409 is checked separately at the handler/DB (assignment FK is Restrict).</summary>
    public void EnsureDeletable()
    {
        if (IsMerchantOwnerSeed)
            throw new InvalidOperationException("The merchant_owner role cannot be deleted.");
    }

    /// <summary>Replaces the granted permissions with the validated, de-duplicated subset of
    /// <paramref name="catalogKeys"/>. Any key outside the catalog is rejected; blanks/duplicates are
    /// dropped. Existing rows are kept (so EF only writes the delta).</summary>
    public void SetPermissions(IEnumerable<string> permissionKeys, IReadOnlySet<string> catalogKeys)
    {
        ArgumentNullException.ThrowIfNull(permissionKeys);
        ArgumentNullException.ThrowIfNull(catalogKeys);

        var desired = permissionKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unknown = desired.Where(k => !catalogKeys.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"Unknown permission keys: {string.Join(", ", unknown)}", nameof(permissionKeys));

        _permissions.RemoveAll(p => !desired.Contains(p.PermissionKey, StringComparer.Ordinal));
        foreach (var key in desired)
            if (!_permissions.Any(p => string.Equals(p.PermissionKey, key, StringComparison.Ordinal)))
                _permissions.Add(new RolePermission(Id, key));
    }

    private static string NormalizeCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var trimmed = code.Trim();
        if (trimmed.Length > 64)
            throw new ArgumentException("Role code must be 64 characters or fewer.", nameof(code));
        if (!CodePattern.IsMatch(trimmed))
            throw new ArgumentException("Role code may only contain lowercase letters, digits, and underscores.", nameof(code));
        return trimmed;
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
