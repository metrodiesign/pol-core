using Payments.Domain.Routing;

namespace Payments.Tests;

public sealed class AdminRoutingRulesetTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConnectionA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectionB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime Now = new(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);

    private static RoutingRuleSpec Rule(int priority, decimal? min, decimal? max) =>
        new(priority, "card", null, min, max, ConnectionA, ConnectionB, true);

    [Fact]
    public void Create_rejects_overlapping_enabled_predicates()
    {
        var exception = Assert.Throws<RoutingOverlapException>(() =>
            RoutingRuleset.Create(MerchantId, "default", [Rule(1, 0, 100), Rule(2, 100, 200)], Now));

        Assert.Equal("Enabled routing predicates overlap.", exception.Message);
    }

    [Fact]
    public void Create_accepts_adjacent_non_overlapping_ranges_and_normalizes_method()
    {
        var ruleset = RoutingRuleset.Create(
            MerchantId, "default", [Rule(1, 0, 99.99m), Rule(2, 100, 200)], Now);

        Assert.Equal([1, 2], ruleset.Rules.Select(x => x.Priority));
        Assert.All(ruleset.Rules, rule => Assert.Equal("card", rule.Method));
        Assert.Equal(RoutingRulesetStatus.Draft, ruleset.Status);
        Assert.Equal(1, ruleset.Version);
    }

    [Fact]
    public void Approval_lifecycle_is_explicit_and_monotonic()
    {
        var ruleset = RoutingRuleset.Create(MerchantId, "default", [Rule(1, null, null)], Now);
        var approval = Guid.NewGuid();

        ruleset.RequestActivation(approval, Now.AddMinutes(1));
        ruleset.Activate(Now.AddMinutes(2));
        ruleset.Supersede(Now.AddMinutes(3));

        Assert.Equal(approval, ruleset.ApprovalId);
        Assert.Equal(RoutingRulesetStatus.Superseded, ruleset.Status);
        Assert.Equal(4, ruleset.Version);
    }
}
