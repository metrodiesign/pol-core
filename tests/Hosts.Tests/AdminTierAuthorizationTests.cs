extern alias ApiHost;

namespace Hosts.Tests;

/// <summary>The Super-only admin tier gate (REQ-8.1), fail-closed. A Scoped admin is denied a Super-only
/// action; an unresolved (null) tier is denied; an unknown tier is denied; only a resolved tier in the allowed
/// set passes. Wired onto the provisioning + admin-management endpoints in Program.cs.</summary>
public sealed class AdminTierAuthorizationTests
{
    private static readonly string[] SuperOnly = ["Super"];

    private static bool Allowed(string? tier) =>
        ApiHost::Api.Admins.TierAuthorization.IsTierAllowed(tier, SuperOnly);

    [Fact] public void Super_is_admitted() => Assert.True(Allowed("Super"));
    [Fact] public void Scoped_is_denied_a_super_only_action() => Assert.False(Allowed("Scoped"));
    [Fact] public void An_unresolved_null_tier_is_denied() => Assert.False(Allowed(null));
    [Fact] public void An_unknown_tier_is_denied() => Assert.False(Allowed("Root"));
}
