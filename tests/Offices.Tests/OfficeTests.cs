using Offices.Domain;

namespace Offices.Tests;

// Domain-invariant tests moved from Admins.Tests/MasterDataAndProfileTests.cs (masterdata-split task 1) —
// same three shapes per entity: create/trim, slug rejection, rename + active toggle.
public sealed class OfficeTests
{
    [Fact]
    public void Create_sets_fields_active_and_trims()
    {
        var m = Office.Create(" hq ", "  Headquarters  ");
        Assert.Equal("hq", m.Code);
        Assert.Equal("Headquarters", m.Name);
        Assert.True(m.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("CEO")]        // uppercase not allowed
    [InlineData("head office")] // space not allowed
    public void Create_rejects_a_bad_code(string code)
    {
        Assert.Throws<ArgumentException>(() => Office.Create(code, "x"));
    }

    [Fact]
    public void Rename_and_toggle_active()
    {
        var m = Office.Create("hq", "Headquarters");
        m.Rename(" Head Office ");
        Assert.Equal("Head Office", m.Name);
        m.Deactivate();
        Assert.False(m.IsActive);
        m.Activate();
        Assert.True(m.IsActive);
    }
}
