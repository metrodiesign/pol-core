using System.Text.RegularExpressions;
using SharedKernel;

namespace MasterData.Domain;

/// <summary>
/// Shared shape for the four admin-profile reference lists: <see cref="Positions.Position"/> (ตำแหน่ง),
/// <see cref="Offices.Office"/> (สถานที่ปฏิบัติงาน), <see cref="Levels.Level"/> (ระดับ), <see cref="Divisions.Division"/>
/// (ฝ่าย/ภาค). Control-plane, admin-managed at runtime (no RLS). <see cref="Code"/> is the stable slug — unique per
/// table and immutable once created; <see cref="Name"/> is the display label. An inactive master stays
/// referenceable by existing accounts but cannot be newly assigned (guarded at the application layer).
/// Each concrete subclass maps to its own table so the FK on <c>User</c> is type-safe.
/// </summary>
public abstract class MasterDataItem : AggregateRoot<Guid>
{
    // Code lands nowhere in a route path, but stays a URL-safe slug for parity with Role.Code.
    private static readonly Regex CodePattern = new("^[a-z0-9_]+$");

    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public bool IsActive { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    protected MasterDataItem() { }

    protected MasterDataItem(Guid id, string code, string name) : base(id)
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

    /// <summary>Renames the display label. The code is immutable (identity).</summary>
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;
}
