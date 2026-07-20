using Divisions.Domain;

namespace Divisions.Tests;

// Domain-invariant tests moved from Admins.Tests/MasterDataAndProfileTests.cs (masterdata-split task 1) —
// same three shapes per entity: create/trim, slug rejection, rename + active toggle.
public sealed class DivisionTests
{
    [Fact]
    public void Create_sets_fields_active_and_trims()
    {
        var m = Division.Create(" executive ", "  Executive  ");
        Assert.Equal("executive", m.Code);
        Assert.Equal("Executive", m.Name);
        Assert.True(m.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("CEO")]        // uppercase not allowed
    [InlineData("head office")] // space not allowed
    public void Create_rejects_a_bad_code(string code)
    {
        Assert.Throws<ArgumentException>(() => Division.Create(code, "x"));
    }

    [Fact]
    public void Rename_and_toggle_active()
    {
        var m = Division.Create("executive", "Executive");
        m.Rename(" North Region ");
        Assert.Equal("North Region", m.Name);
        m.Deactivate();
        Assert.False(m.IsActive);
        m.Activate();
        Assert.True(m.IsActive);
    }
}
