using System.Text.RegularExpressions;
using SharedKernel;

namespace Offices.Domain;

/// <summary>
/// สถานที่ปฏิบัติงาน — the office/work location an admin is based at. Control-plane reference data, admin-managed at runtime.
/// <see cref="Code"/> is the stable slug — unique in <c>cfg.Offices</c> and immutable once created;
/// <see cref="Name"/> is the display label. An inactive office stays referenceable by existing accounts
/// but cannot be newly assigned (guarded at the application layer). Standalone aggregate since
/// masterdata-split — the retired shared base logic lives inline, verbatim.
/// </summary>
public sealed class Office : AggregateRoot<Guid>
{
    // Code lands nowhere in a route path, but stays a URL-safe slug for parity with Role.Code.
    private static readonly Regex CodePattern = new("^[a-z0-9_]+$");

    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public bool IsActive { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Office() { }

    private Office(Guid id, string code, string name) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        code = code.Trim();
        if (!CodePattern.IsMatch(code))
            throw new ArgumentException("Code must match ^[a-z0-9_]+$.", nameof(code));
        Code = code;
        Name = name.Trim();
        IsActive = true;
    }

    public static Office Create(string code, string name) => new(Guid.NewGuid(), code, name);

    /// <summary>Renames the display label. The code is immutable (identity).</summary>
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;
}
