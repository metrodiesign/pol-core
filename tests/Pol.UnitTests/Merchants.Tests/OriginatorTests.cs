using Merchants.Domain;

namespace Merchants.Tests;

public sealed class OriginatorTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Now = new(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(OriginatorType.Branch)]
    [InlineData(OriginatorType.Agent)]
    [InlineData(OriginatorType.Broker)]
    [InlineData(OriginatorType.Staff)]
    [InlineData(OriginatorType.App)]
    public void Create_supports_every_approved_originator_type(OriginatorType type)
    {
        var originator = Originator.Create(MerchantId, " Branch_01 ", " Bangkok ", type, " S01 ", null, Now);

        Assert.Equal(MerchantId, originator.MerchantId);
        Assert.Equal("branch_01", originator.Code);
        Assert.Equal("Bangkok", originator.Name);
        Assert.Equal(type, originator.Type);
        Assert.Equal(OriginatorStatus.Active, originator.Status);
        Assert.Equal(1, originator.Version);
    }

    [Fact]
    public void State_changes_are_idempotent_and_monotonic()
    {
        var originator = Originator.Create(MerchantId, "app", "App", OriginatorType.App, null, null, Now);

        originator.Disable(Now.AddMinutes(1));
        originator.Disable(Now.AddMinutes(2));
        originator.Enable(Now.AddMinutes(3));

        Assert.Equal(OriginatorStatus.Active, originator.Status);
        Assert.Equal(Now.AddMinutes(3), originator.UpdatedAt);
        Assert.Equal(3, originator.Version);
    }

    [Theory]
    [InlineData("bad code")]
    [InlineData("slash/code")]
    public void Create_rejects_unsupported_code_characters(string code) =>
        Assert.Throws<ArgumentException>(() =>
            Originator.Create(MerchantId, code, "Name", OriginatorType.Branch, null, null, Now));
}
