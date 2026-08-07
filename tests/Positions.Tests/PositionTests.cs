using Positions.Domain;

namespace Positions.Tests;

// Domain-invariant tests moved from Admins.Tests/MasterDataAndProfileTests.cs (masterdata-split task 1) —
// same three shapes per entity: create/trim, slug rejection, rename + active toggle.
public sealed class PositionTests
{
    [Fact]
    public void Create_sets_fields_active_and_trims()
    {
        var m = Position.Create(" ceo ", "  Chief Executive  ");
        Assert.Equal("ceo", m.Code);
        Assert.Equal("Chief Executive", m.Name);
        Assert.Equal(PositionStatus.Active, m.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("CEO")]        // uppercase not allowed
    [InlineData("head office")] // space not allowed
    public void Create_rejects_a_bad_code(string code)
    {
        Assert.Throws<ArgumentException>(() => Position.Create(code, "x"));
    }

    [Fact]
    public void Rename_and_toggle_active()
    {
        var m = Position.Create("ceo", "Chief Executive");
        m.Rename(" Chief Exec ");
        Assert.Equal("Chief Exec", m.Name);
        m.Deactivate();
        Assert.Equal(PositionStatus.Inactive, m.Status);
        m.Activate();
        Assert.Equal(PositionStatus.Active, m.Status);
    }
}
