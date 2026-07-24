extern alias ApiHost;
using Admins.Application.Users;
using Admins.Domain.Users;

namespace Hosts.Tests;

/// <summary>The unified permission gate (rf2 REQ-4.1/4.2/4.3), fail-closed: whichever scope is bound — admin
/// checked first, then merchant-user — must hold the required key; neither bound, or the bound one missing the
/// key, is denied — never a 500. Supersedes the old separate AdminPermissionAuthorizationTests/
/// MerchantUserPermissionAuthorizationTests (two gates -> one).</summary>
public sealed class PermissionAuthorizationTests
{
    private static ApiHost::Api.Admins.AdminScope AdminBoundScope(params string[] permissions)
    {
        var scope = new ApiHost::Api.Admins.AdminScope();
        scope.Set(new Resolution(Guid.NewGuid(), "a@org.com", Tier.Scoped, AccessibleMerchants.All)
        {
            Permissions = permissions.ToHashSet(),
        });
        return scope;
    }

    private static ApiHost::Api.Merchants.UserScope UserBoundScope(params string[] permissions)
    {
        var scope = new ApiHost::Api.Merchants.UserScope();
        scope.Set(new Merchants.Application.Users.Resolution(
            Guid.NewGuid(), "p@org.com", Guid.NewGuid(), permissions.ToHashSet(StringComparer.Ordinal)));
        return scope;
    }

    [Fact]
    public void Admits_a_bound_admin_whose_effective_set_holds_the_permission() =>
        Assert.True(ApiHost::Api.Iam.PermissionAuthorization.IsAllowed(
            AdminBoundScope("user.roles", "txn.view"), new ApiHost::Api.Merchants.UserScope(), "user.roles"));

    [Fact]
    public void Denies_a_bound_admin_missing_the_permission() =>
        Assert.False(ApiHost::Api.Iam.PermissionAuthorization.IsAllowed(
            AdminBoundScope("txn.view"), new ApiHost::Api.Merchants.UserScope(), "user.roles"));

    [Fact]
    public void Admits_a_bound_merchant_user_whose_effective_set_holds_the_permission() =>
        Assert.True(ApiHost::Api.Iam.PermissionAuthorization.IsAllowed(
            new ApiHost::Api.Admins.AdminScope(), UserBoundScope("roles.manage", "product.create"), "roles.manage"));

    [Fact]
    public void Denies_a_bound_merchant_user_missing_the_permission() =>
        Assert.False(ApiHost::Api.Iam.PermissionAuthorization.IsAllowed(
            new ApiHost::Api.Admins.AdminScope(), UserBoundScope("product.create"), "roles.manage"));

    [Fact]
    public void Fails_closed_when_neither_scope_is_bound() =>
        Assert.False(ApiHost::Api.Iam.PermissionAuthorization.IsAllowed(
            new ApiHost::Api.Admins.AdminScope(), new ApiHost::Api.Merchants.UserScope(), "user.roles"));

    [Fact]
    public void An_admin_bound_scope_is_checked_before_the_merchant_user_scope() =>
        // Both happen to be bound (should never occur given mutually-exclusive auth schemes) — admin wins,
        // documenting the fixed precedence rather than leaving it to call order.
        Assert.True(ApiHost::Api.Iam.PermissionAuthorization.IsAllowed(
            AdminBoundScope("user.roles"), UserBoundScope(), "user.roles"));
}

/// <summary>The boot parity guard (rf2 REQ-5.1/5.4), side-aware: a gate key must be catalogued AND its side
/// (<c>Keys.KeySide</c>) must match the <c>Scope</c> its endpoint's own policy implies. Supersedes the old
/// separate AdminPermissionParityTests/MerchantUserPermissionParityTests (set-only, side-blind).</summary>
public sealed class PermissionParityTests
{
    // The real (key, policy) gate-site pairs (PermissionGateSitesTests pins the endpoint inventory itself) —
    // every one must pass parity: catalogued, and on the side its policy implies.
    private static readonly (string Key, string? Policy)[] RealGateSites =
    [
        ("product.create", "merchant-user"), ("payment.create", "merchant-user"), ("payment.redirect", "merchant-user"),
        ("roles.manage", "merchant-user"), ("users.roles", "merchant-user"), ("policies.write", "merchant-user"),
        ("policies.read", "merchant-user"),
        ("merchants.users.approve", "admin"), ("merchants.users.reject", "admin"),
        ("user.view", "admin"), ("user.manage", "admin"), ("user.roles", "admin"),
        ("merchants.policies.write", "admin"), ("merchants.policies.read", "admin"),
    ];

    [Fact]
    public void Every_real_gate_site_passes_parity() =>
        Assert.Empty(ApiHost::Api.Iam.PermissionParity.FindProblems(RealGateSites));

    [Fact]
    public void A_key_outside_the_catalog_is_flagged() =>
        Assert.Contains(ApiHost::Api.Iam.PermissionParity.FindProblems([("bogus.key", "admin")]),
            p => p.Contains("bogus.key", StringComparison.Ordinal) && p.Contains("catalog", StringComparison.Ordinal));

    [Fact]
    public void A_platform_key_gated_under_the_merchant_user_policy_is_flagged() =>
        Assert.Contains(ApiHost::Api.Iam.PermissionParity.FindProblems([("user.roles", "merchant-user")]),
            p => p.Contains("user.roles", StringComparison.Ordinal));

    [Fact]
    public void A_merchant_key_gated_under_the_admin_policy_is_flagged() =>
        Assert.Contains(ApiHost::Api.Iam.PermissionParity.FindProblems([("roles.manage", "admin")]),
            p => p.Contains("roles.manage", StringComparison.Ordinal));

    [Fact]
    public void An_unrecognized_policy_is_flagged() =>
        Assert.Contains(ApiHost::Api.Iam.PermissionParity.FindProblems([("user.roles", "some-other-policy")]),
            p => p.Contains("unrecognized", StringComparison.Ordinal));

    [Fact]
    public void A_null_policy_ie_no_authorization_data_is_flagged() =>
        Assert.Contains(ApiHost::Api.Iam.PermissionParity.FindProblems([("user.roles", null)]),
            p => p.Contains("unrecognized", StringComparison.Ordinal));
}
