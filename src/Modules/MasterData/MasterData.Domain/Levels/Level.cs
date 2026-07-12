using MasterData.Domain;

namespace MasterData.Domain.Levels;

/// <summary>ระดับ — the grade/level of an admin.</summary>
public sealed class Level : MasterDataItem
{
    private Level() { }
    private Level(string code, string name) : base(Guid.NewGuid(), code, name) { }
    public static Level Create(string code, string name) => new(code, name);
}
