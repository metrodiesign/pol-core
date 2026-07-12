using System.Text.RegularExpressions;
using Iam.Domain.Permissions;
using SharedKernel;

namespace Iam.Domain.Roles;

/// <summary>
/// A named permission set (REQ-1/2), the single catalog aggregate replacing the two duplicated
/// <c>Admins.Domain.Roles.Role</c> / <c>Merchants.Domain.Users.Roles.Role</c> types. <see cref="Code"/> is the
/// stable, immutable identity; granted permissions are always a subset of the catalog AND must share the
/// role's own <see cref="Scope"/> (REQ-6.6 — a cross-side grant is structurally impossible). <see cref="Scope"/>
/// is immutable (REQ-3.1); a Platform-scope role can never carry a <see cref="MerchantId"/> (REQ-3.2/3.3) — NULL
/// means shared/seed, a value means a merchant's own custom role. An Inactive role contributes nothing to an
/// actor's effective permissions (REQ-4.6). The two seed anchors — <see cref="PlatformAdminCode"/> and
/// <see cref="MerchantManagerCode"/> — are the lockout-recovery roles and can never be deactivated or deleted
/// (REQ-2.4).
/// </summary>
public sealed class Role : AggregateRoot<Guid>
{
    /// <summary>The seed role granted to the bootstrap Super; the Platform-side recovery anchor.</summary>
    public const string PlatformAdminCode = "platform_admin";

    /// <summary>The seed role granting every merchant key; the Merchant-side recovery anchor.</summary>
    public const string MerchantManagerCode = "merchant_manager";

    // Code lands in route paths (GET /admins/roles/{code}); constrain to a URL-safe slug so it can never carry
    // '/', '?', '#', '%' etc. All seeded codes are lowercase snake_case.
    private static readonly Regex CodePattern = new("^[a-z0-9_]+$");

    private readonly List<RolePermission> _permissions = [];

    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public string? Color { get; private set; }
    public RoleStatus Status { get; private set; }

    /// <summary>Which console this role belongs to (REQ-3.1). Immutable — set only at <see cref="Create"/>.</summary>
    public Scope Scope { get; private set; }

    /// <summary>NULL = shared/seed role; a value = a custom role owned by that merchant (REQ-3.2). Always NULL
    /// when <see cref="Scope"/> is Platform (REQ-3.3, enforced here AND by the DB CHECK constraint).</summary>
    public Guid? MerchantId { get; private set; }

    /// <summary>The granted permission rows (EF navigation, backed by <c>_permissions</c>).</summary>
    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    /// <summary>The granted permission keys.</summary>
    public IReadOnlyCollection<string> PermissionKeys => [.. _permissions.Select(p => p.PermissionKey)];

    /// <summary>Whether this is one of the two undeletable/undeactivatable recovery anchors (REQ-2.4).
    /// Anchored to the SEEDED row, not the code alone: only a shared/NULL-bucket role can be a seed anchor
    /// (the duplicate pre-check bars any second NULL-bucket row with these codes), so a merchant's own custom
    /// role that happens to reuse the code — which the merchant CAN create, since the Platform seed is outside
    /// its visible set and the unique index buckets by MerchantId — stays freely deactivatable/deletable.</summary>
    public bool IsSeedAnchor =>
        MerchantId is null &&
        (string.Equals(Code, PlatformAdminCode, StringComparison.Ordinal) ||
         string.Equals(Code, MerchantManagerCode, StringComparison.Ordinal));

    private Role() { }

    private Role(Guid id, string code, string name, string? description, string? color, RoleStatus status,
        Scope scope, Guid? merchantId) : base(id)
    {
        Code = code;
        Name = name;
        Description = description;
        Color = color;
        Status = status;
        Scope = scope;
        MerchantId = merchantId;
    }

    /// <summary>Creates a role with a validated permission subset. <paramref name="catalog"/> maps every valid
    /// catalog key to its <see cref="Scope"/> (<c>Keys.KeySide</c>) — a key outside the catalog, or one whose
    /// side does not match <paramref name="scope"/>, is rejected (REQ-2.6/6.6).</summary>
    public static Role Create(string code, string name, string? description, string? color,
        RoleStatus status, Scope scope, Guid? merchantId,
        IEnumerable<string> permissionKeys, IReadOnlyDictionary<string, Scope> catalog)
    {
        if (scope == Scope.Platform && merchantId is not null)
            throw new ArgumentException("A Platform-scope role cannot carry a MerchantId.", nameof(merchantId));

        var role = new Role(Guid.NewGuid(), NormalizeCode(code), NormalizeName(name),
            Trim(description), Trim(color), status, scope, merchantId);
        role.SetPermissions(permissionKeys, catalog);
        return role;
    }

    public void Rename(string name) => Name = NormalizeName(name);

    public void SetDescription(string? description) => Description = Trim(description);

    public void SetColor(string? color) => Color = Trim(color);

    public void Activate() => Status = RoleStatus.Active;

    /// <summary>Deactivates the role. A seed anchor is the recovery anchor for its side and may never be
    /// deactivated (REQ-2.4).</summary>
    public void Deactivate()
    {
        if (IsSeedAnchor)
            throw new InvalidOperationException($"The {Code} role cannot be deactivated.");
        Status = RoleStatus.Inactive;
    }

    /// <summary>Guards deletion: a seed anchor may never be deleted (REQ-2.4). The "still has ≥1 assignment"
    /// 409 is checked separately at the store/handler (assignment FK is Restrict).</summary>
    public void EnsureDeletable()
    {
        if (IsSeedAnchor)
            throw new InvalidOperationException($"The {Code} role cannot be deleted.");
    }

    /// <summary>Replaces the granted permissions with the validated, de-duplicated subset of
    /// <paramref name="catalog"/>'s keys. Any key outside the catalog, OR whose side does not match this role's
    /// own <see cref="Scope"/>, is rejected (REQ-2.6/6.6); blanks/duplicates are dropped. Existing rows are kept
    /// (so EF only writes the delta).</summary>
    public void SetPermissions(IEnumerable<string> permissionKeys, IReadOnlyDictionary<string, Scope> catalog)
    {
        ArgumentNullException.ThrowIfNull(permissionKeys);
        ArgumentNullException.ThrowIfNull(catalog);

        var desired = permissionKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unknown = desired.Where(k => !catalog.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"Unknown permission keys: {string.Join(", ", unknown)}", nameof(permissionKeys));

        var wrongSide = desired.Where(k => catalog[k] != Scope).ToList();
        if (wrongSide.Count > 0)
            throw new ArgumentException(
                $"Permission key(s) outside this role's scope ({Scope}): {string.Join(", ", wrongSide)}", nameof(permissionKeys));

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
