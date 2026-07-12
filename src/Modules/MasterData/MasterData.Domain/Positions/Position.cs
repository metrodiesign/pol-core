using MasterData.Domain;

namespace MasterData.Domain.Positions;

/// <summary>ตำแหน่ง — the job title an admin holds.</summary>
public sealed class Position : MasterDataItem
{
    private Position() { }
    private Position(string code, string name) : base(Guid.NewGuid(), code, name) { }
    public static Position Create(string code, string name) => new(code, name);
}
