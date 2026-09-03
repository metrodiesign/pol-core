using System.Text.RegularExpressions;
using SharedKernel;

namespace Divisions.Domain;

/// <summary>
/// ฝ่าย/ภาค — the division/region an admin belongs to. Control-plane reference data, admin-managed at runtime.
/// <see cref="Code"/> is the stable slug — unique in <c>cfg.Divisions</c> and immutable once created;
/// <see cref="Name"/> is the display label. An inactive division stays referenceable by existing accounts
/// but cannot be newly assigned (guarded at the application layer). Standalone aggregate since
/// masterdata-split — the retired shared base logic lives inline, verbatim.
/// </summary>
public sealed class Division : AggregateRoot<Guid>
{
    // Code lands nowhere in a route path, but stays a URL-safe slug for parity with Role.Code.
    private static readonly Regex CodePattern = new("^[a-z0-9_]+$");

    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public DivisionStatus Status { get; private set; }
    public long Version { get; private set; }

    /// <summary>Legacy source key (<c>cfg.VibEmp.DepartmentID</c>) an operator maps via SQL (tier0-graph-employee-profile
    /// REQ-6); NULL = unmapped. No mutator in code by design — filtered unique in <c>cfg.Divisions</c>.</summary>
    public string? LegacyKey { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Division() { }

    private Division(Guid id, string code, string name) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        code = code.Trim();
        if (!CodePattern.IsMatch(code))
            throw new ArgumentException("Code must match ^[a-z0-9_]+$.", nameof(code));
        Code = code;
        Name = name.Trim();
        Status = DivisionStatus.Active;
        Version = 1;
    }

    public static Division Create(string code, string name) => new(Guid.NewGuid(), code, name);

    /// <summary>Renames the display label. The code is immutable (identity).</summary>
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void Activate() => Status = DivisionStatus.Active;
    public void Deactivate() => Status = DivisionStatus.Inactive;
    public void BumpVersion() => Version++;
}
