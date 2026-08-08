using Iam.Domain.Permissions;

namespace Iam.Tests;

/// <summary>Literal pins for the central catalog vocabulary (REQ-10.1) — a rename or a silent add/drop of a
/// key/group must fail here first, before it can ever reach the DB seed or a gate site.</summary>
public sealed class KeysTests
{
    private static readonly string[] ExpectedKeys =
    [
        "txn.view", "txn.refund", "txn.export",
        "merchant.view", "merchant.manage",
        "user.view", "user.manage", "user.roles",
        "audit.view", "settings.manage", "apikey.manage",
        "merchants.users.approve", "merchants.users.reject", "merchants.users.view",
        "payment.create", "payment.redirect",
        "roles.view", "roles.manage", "users.roles",
    ];

    private static readonly string[] ExpectedGroups =
        ["txn", "merchant", "user", "system", "merchants.users", "payment", "roles"];

    [Fact]
    public void All_has_exactly_19_keys_across_7_groups()
    {
        Assert.Equal(19, Keys.AllKeys.Count);
        Assert.Equal(7, Keys.GroupKeys.Count);
        Assert.Equal(ExpectedKeys.ToHashSet(StringComparer.Ordinal), Keys.AllKeys);
        Assert.Equal(ExpectedGroups.ToHashSet(StringComparer.Ordinal), Keys.GroupKeys.ToHashSet(StringComparer.Ordinal));
        Assert.All(Keys.All, p => Assert.Contains(p.GroupKey, Keys.GroupKeys));
    }

    // No dead keys carried over from the two prior catalogs (REQ-2.2): invoice.view/invoice.manage/settlement.run
    // and the finance group must never reappear.
    [Fact]
    public void Dropped_finance_keys_never_reappear()
    {
        Assert.DoesNotContain("invoice.view", Keys.AllKeys);
        Assert.DoesNotContain("invoice.manage", Keys.AllKeys);
        Assert.DoesNotContain("settlement.run", Keys.AllKeys);
        Assert.DoesNotContain("finance", Keys.GroupKeys);
    }

    [Theory]
    [InlineData("txn", Scope.Platform)]
    [InlineData("merchant", Scope.Platform)]
    [InlineData("user", Scope.Platform)]
    [InlineData("system", Scope.Platform)]
    [InlineData("merchants.users", Scope.Platform)]
    [InlineData("payment", Scope.Merchant)]
    [InlineData("roles", Scope.Merchant)]
    public void GroupScope_matches_the_v5_plan(string group, Scope expected) =>
        Assert.Equal(expected, Keys.GroupScope[group]);

    [Fact]
    public void KeySide_covers_every_key_and_matches_its_group()
    {
        Assert.Equal(Keys.AllKeys.Count, Keys.KeySide.Count);
        foreach (var (key, groupKey) in Keys.All)
            Assert.Equal(Keys.GroupScope[groupKey], Keys.KeySide[key]);
    }

    [Fact]
    public void Platform_side_has_14_keys_and_merchant_side_has_5()
    {
        Assert.Equal(14, Keys.KeySide.Count(kv => kv.Value == Scope.Platform));
        Assert.Equal(5, Keys.KeySide.Count(kv => kv.Value == Scope.Merchant));
    }

    // user.roles (admin) and users.roles (merchant) are near-identical literals that used to live in two separate
    // catalogs; merged into one class they must stay distinct AND both survive (REQ-10.4 — guards the exact
    // swap risk a set-only check would miss).
    [Fact]
    public void UserRoles_and_UsersRoles_are_distinct_literals_on_opposite_sides()
    {
        Assert.Equal("user.roles", Keys.UserRoles);
        Assert.Equal("users.roles", Keys.UsersRoles);
        Assert.NotEqual(Keys.UserRoles, Keys.UsersRoles);
        Assert.Equal(Scope.Platform, Keys.KeySide[Keys.UserRoles]);
        Assert.Equal(Scope.Merchant, Keys.KeySide[Keys.UsersRoles]);
    }
}
