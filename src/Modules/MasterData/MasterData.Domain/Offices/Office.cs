using MasterData.Domain;

namespace MasterData.Domain.Offices;

/// <summary>สถานที่ปฏิบัติงาน — the office/work location an admin is based at.</summary>
public sealed class Office : MasterDataItem
{
    private Office() { }
    private Office(string code, string name) : base(Guid.NewGuid(), code, name) { }
    public static Office Create(string code, string name) => new(code, name);
}
