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
        Assert.Equal(DivisionStatus.Active, m.Status);
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
        Assert.Equal(DivisionStatus.Inactive, m.Status);
        m.Activate();
        Assert.Equal(DivisionStatus.Active, m.Status);
    }

    [Fact]
    public void Resource_version_starts_at_one_and_bumps_monotonically()
    {
        var m = Division.Create("executive", "Executive");
        Assert.Equal(1, m.Version);
        m.BumpVersion();
        Assert.Equal(2, m.Version);
    }
}
