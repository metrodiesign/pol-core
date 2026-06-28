using System.Text.RegularExpressions;
using SharedKernel;

namespace Producer.Domain;

/// <summary>
/// A named, admin-managed permission set for producers (REQ-16). Control-plane. <see cref="Code"/> is the stable,
/// immutable identity (REQ-16.1); granted permissions are always a subset of the catalog (REQ-16.2). An Inactive
/// role contributes nothing to a producer's effective permissions (REQ-16.4). The seed <see cref="TenantOwnerCode"/>
/// role grants every producer key as the deliberate recovery anchor and cannot be deactivated or deleted (REQ-16.5);
/// <see cref="TenantMemberCode"/> is the ordinary default approval choice (product/payment only).
/// </summary>
// ponytail: DUPLICATE of Admin.Domain.AdminRole — deliberate debt, do not refactor into a shared base.
public sealed class ProducerRole : AggregateRoot<Guid>
{
    /// <summary>The seed role granting ALL producer keys; the deliberate anchor that may not be deactivated or
    /// deleted (REQ-16.5).</summary>
    public const string TenantOwnerCode = "tenant_owner";

    /// <summary>The ordinary default approval role (product/payment only, no role-management) — NOT an anchor.</summary>
    public const string TenantMemberCode = "tenant_member";

    // Code lands in route paths (GET /producer/roles/{code}); constrain to a URL-safe slug so it can never carry
    // '/', '?', '#', '%' etc. All seeded codes are lowercase snake_case.
    private static readonly Regex CodePattern = new("^[a-z0-9_]+$");

    private readonly List<ProducerRolePermission> _permissions = [];

    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public string? Color { get; private set; }
    public ProducerRoleStatus Status { get; private set; }

    /// <summary>The granted permission rows (EF navigation, backed by <c>_permissions</c>).</summary>
    public IReadOnlyCollection<ProducerRolePermission> Permissions => _permissions.AsReadOnly();

    /// <summary>The granted permission keys.</summary>
    public IReadOnlyCollection<string> PermissionKeys => [.. _permissions.Select(p => p.PermissionKey)];

    public bool IsTenantOwnerSeed => string.Equals(Code, TenantOwnerCode, StringComparison.Ordinal);

    private ProducerRole() { }

    private ProducerRole(Guid id, string code, string name, string? description, string? color, ProducerRoleStatus status)
        : base(id)
    {
        Code = code;
        Name = name;
        Description = description;
        Color = color;
        Status = status;
    }

    /// <summary>Creates a role with a validated permission subset. <paramref name="catalogKeys"/> is the live
    /// catalog vocabulary; any key outside it is rejected (REQ-16.2).</summary>
    public static ProducerRole Create(string code, string name, string? description, string? color,
        ProducerRoleStatus status, IEnumerable<string> permissionKeys, IReadOnlySet<string> catalogKeys)
    {
        var role = new ProducerRole(Guid.NewGuid(), NormalizeCode(code), NormalizeName(name),
            Trim(description), Trim(color), status);
        role.SetPermissions(permissionKeys, catalogKeys);
        return role;
    }

    public void Rename(string name) => Name = NormalizeName(name);

    public void SetDescription(string? description) => Description = Trim(description);

    public void SetColor(string? color) => Color = Trim(color);

    public void Activate() => Status = ProducerRoleStatus.Active;

    /// <summary>Deactivates the role. The <see cref="TenantOwnerCode"/> seed is the recovery anchor and may never
    /// be deactivated (REQ-16.5).</summary>
    public void Deactivate()
    {
        if (IsTenantOwnerSeed)
            throw new InvalidOperationException("The tenant_owner role cannot be deactivated.");
        Status = ProducerRoleStatus.Inactive;
    }

    /// <summary>Guards deletion: the <see cref="TenantOwnerCode"/> anchor may never be deleted (REQ-16.5). The
    /// "still has ≥1 assignment" 409 is checked separately at the handler/DB (assignment FK is Restrict).</summary>
    public void EnsureDeletable()
    {
        if (IsTenantOwnerSeed)
            throw new InvalidOperationException("The tenant_owner role cannot be deleted.");
    }

    /// <summary>Replaces the granted permissions with the validated, de-duplicated subset of
    /// <paramref name="catalogKeys"/> (REQ-16.2). Any key outside the catalog is rejected; blanks/duplicates are
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
                _permissions.Add(new ProducerRolePermission(Id, key));
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
