using MasterData.Domain;

namespace MasterData.Domain.Divisions;

/// <summary>ฝ่าย/ภาค — the division/region an admin belongs to.</summary>
public sealed class Division : MasterDataItem
{
    private Division() { }
    private Division(string code, string name) : base(Guid.NewGuid(), code, name) { }
    public static Division Create(string code, string name) => new(code, name);
}
