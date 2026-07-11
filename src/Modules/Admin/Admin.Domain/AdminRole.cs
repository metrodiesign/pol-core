using System.Text.RegularExpressions;
using SharedKernel;

namespace Admin.Domain;

/// <summary>
/// A named, admin-managed permission set in the Admin Console (REQ-2). Control-plane and ORTHOGONAL to
/// <see cref="PlatformUserTier"/>: a role grants ACTIONS (permission keys), independent of an admin's merchant-reach.
/// <see cref="Code"/> is the stable, immutable identity (REQ-2.4); granted permissions are always a subset of the
/// catalog (REQ-3.3). An Inactive role contributes nothing to an admin's effective permissions (REQ-5.2). The seed
/// <see cref="SuperAdminCode"/> role is the lockout-recovery anchor and cannot be deactivated (REQ-8.3).
/// </summary>
public sealed class AdminRole : AggregateRoot<Guid>
{
    /// <summary>The seed role granted to the bootstrap Super; the recovery anchor that may not be deactivated.</summary>
    public const string SuperAdminCode = "super_admin";

    // Code lands in route paths (GET /admin/roles/{code}); constrain to a URL-safe slug so it can never carry
    // '/', '?', '#', '%' etc. All seeded codes are lowercase snake_case.
    private static readonly Regex CodePattern = new("^[a-z0-9_]+$");

    private readonly List<AdminRolePermission> _permissions = [];

    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public string? Color { get; private set; }
    public AdminRoleStatus Status { get; private set; }

    /// <summary>The granted permission rows (EF navigation, backed by <c>_permissions</c>).</summary>
    public IReadOnlyCollection<AdminRolePermission> Permissions => _permissions.AsReadOnly();

    /// <summary>The granted permission keys.</summary>
    public IReadOnlyCollection<string> PermissionKeys => [.. _permissions.Select(p => p.PermissionKey)];

    public bool IsSuperAdminSeed => string.Equals(Code, SuperAdminCode, StringComparison.Ordinal);

    private AdminRole() { }

    private AdminRole(Guid id, string code, string name, string? description, string? color, AdminRoleStatus status)
        : base(id)
    {
        Code = code;
        Name = name;
        Description = description;
        Color = color;
        Status = status;
    }

    /// <summary>Creates a role with a validated permission subset. <paramref name="catalogKeys"/> is the live
    /// catalog vocabulary; any key outside it is rejected (REQ-3.3).</summary>
    public static AdminRole Create(string code, string name, string? description, string? color,
        AdminRoleStatus status, IEnumerable<string> permissionKeys, IReadOnlySet<string> catalogKeys)
    {
        var role = new AdminRole(Guid.NewGuid(), NormalizeCode(code), NormalizeName(name),
            Trim(description), Trim(color), status);
        role.SetPermissions(permissionKeys, catalogKeys);
        return role;
    }

    public void Rename(string name) => Name = NormalizeName(name);

    public void SetDescription(string? description) => Description = Trim(description);

    public void SetColor(string? color) => Color = Trim(color);

    public void Activate() => Status = AdminRoleStatus.Active;

    /// <summary>Deactivates the role. The <see cref="SuperAdminCode"/> seed is the recovery anchor and may never
    /// be deactivated (REQ-8.3).</summary>
    public void Deactivate()
    {
        if (IsSuperAdminSeed)
            throw new InvalidOperationException("The super_admin role cannot be deactivated.");
        Status = AdminRoleStatus.Inactive;
    }

    /// <summary>Replaces the granted permissions with the validated, de-duplicated subset of
    /// <paramref name="catalogKeys"/> (REQ-3.3). Any key outside the catalog is rejected; blanks/duplicates are
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
                _permissions.Add(new AdminRolePermission(Id, key));
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
