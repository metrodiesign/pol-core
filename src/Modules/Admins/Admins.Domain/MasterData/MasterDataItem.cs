using System.Text.RegularExpressions;
using SharedKernel;

namespace Admins.Domain.MasterData;

/// <summary>
/// Shared shape for the four admin-profile reference lists: <see cref="Position"/> (ตำแหน่ง),
/// <see cref="Office"/> (สถานที่ปฏิบัติงาน), <see cref="Level"/> (ระดับ), <see cref="Division"/> (ฝ่าย/ภาค).
/// Control-plane, admin-managed at runtime (no RLS). <see cref="Code"/> is the stable slug — unique per
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

/// <summary>ตำแหน่ง — the job title an admin holds.</summary>
public sealed class Position : MasterDataItem
{
    private Position() { }
    private Position(string code, string name) : base(Guid.NewGuid(), code, name) { }
    public static Position Create(string code, string name) => new(code, name);
}

/// <summary>สถานที่ปฏิบัติงาน — the office/work location an admin is based at.</summary>
public sealed class Office : MasterDataItem
{
    private Office() { }
    private Office(string code, string name) : base(Guid.NewGuid(), code, name) { }
    public static Office Create(string code, string name) => new(code, name);
}

/// <summary>ระดับ — the grade/level of an admin.</summary>
public sealed class Level : MasterDataItem
{
    private Level() { }
    private Level(string code, string name) : base(Guid.NewGuid(), code, name) { }
    public static Level Create(string code, string name) => new(code, name);
}

/// <summary>ฝ่าย/ภาค — the division/region an admin belongs to.</summary>
public sealed class Division : MasterDataItem
{
    private Division() { }
    private Division(string code, string name) : base(Guid.NewGuid(), code, name) { }
    public static Division Create(string code, string name) => new(code, name);
}
