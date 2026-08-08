using Levels.Domain;

namespace Levels.Tests;

// Domain-invariant tests moved from Admins.Tests/MasterDataAndProfileTests.cs (masterdata-split task 1) —
// same three shapes per entity: create/trim, slug rejection, rename + active toggle.
public sealed class LevelTests
{
    [Fact]
    public void Create_sets_fields_active_and_trims()
    {
        var m = Level.Create(" level_1 ", "  Level 1  ");
        Assert.Equal("level_1", m.Code);
        Assert.Equal("Level 1", m.Name);
        Assert.Equal(LevelStatus.Active, m.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("CEO")]        // uppercase not allowed
    [InlineData("head office")] // space not allowed
    public void Create_rejects_a_bad_code(string code)
    {
        Assert.Throws<ArgumentException>(() => Level.Create(code, "x"));
    }

    [Fact]
    public void Rename_and_toggle_active()
    {
        var m = Level.Create("level_1", "Level 1");
        m.Rename(" C-Seven ");
        Assert.Equal("C-Seven", m.Name);
        m.Deactivate();
        Assert.Equal(LevelStatus.Inactive, m.Status);
        m.Activate();
        Assert.Equal(LevelStatus.Active, m.Status);
    }
}
