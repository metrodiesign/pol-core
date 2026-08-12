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
            new ApiHost::Api.Admins.AdminScope(), UserBoundScope("roles.manage", "payment.create"), "roles.manage"));

    [Fact]
    public void Denies_a_bound_merchant_user_missing_the_permission() =>
        Assert.False(ApiHost::Api.Iam.PermissionAuthorization.IsAllowed(
            new ApiHost::Api.Admins.AdminScope(), UserBoundScope("payment.create"), "roles.manage"));

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

    [Fact]
    public void Audience_gate_uses_the_selected_admin_key_even_when_both_scopes_are_bound() =>
        Assert.True(ApiHost::Api.Iam.AudiencePermissionAuthorization.IsAllowed(
            new ApiHost::Api.Iam.SelectedConsoleAudience(ApiHost::Api.Iam.ConsoleAudience.Admin),
            AdminBoundScope("txn.manage"), UserBoundScope("payment.create"),
            "txn.manage", "payment.create"));

    [Fact]
    public void Audience_gate_uses_the_selected_merchant_key() =>
        Assert.True(ApiHost::Api.Iam.AudiencePermissionAuthorization.IsAllowed(
            new ApiHost::Api.Iam.SelectedConsoleAudience(ApiHost::Api.Iam.ConsoleAudience.Merchant),
            AdminBoundScope(), UserBoundScope("payment.create"),
            "txn.manage", "payment.create"));

    [Fact]
    public void Audience_gate_fails_closed_without_a_selected_audience() =>
        Assert.False(ApiHost::Api.Iam.AudiencePermissionAuthorization.IsAllowed(
            selected: null, AdminBoundScope("txn.manage"), UserBoundScope("payment.create"),
            "txn.manage", "payment.create"));
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
        ("payment.view", "merchant-user"), ("payment.create", "merchant-user"),
        ("payment.redirect", "merchant-user"), ("roles.view", "merchant-user"),
        ("roles.manage", "merchant-user"), ("users.view", "merchant-user"),
        ("users.manage", "merchant-user"), ("users.roles", "merchant-user"),
        ("merchants.users.approve", "admin"), ("merchants.users.reject", "admin"),
        ("merchants.users.view", "admin"),
        ("user.view", "admin"), ("user.manage", "admin"), ("user.roles", "admin"),
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

    [Fact]
    public void A_valid_dual_console_permission_pair_passes_parity() =>
        Assert.Empty(ApiHost::Api.Iam.PermissionParity.FindAudienceProblems(
            [("txn.manage", "payment.create", "dual-console")]));

    [Fact]
    public void A_swapped_dual_console_permission_pair_is_flagged() =>
        Assert.NotEmpty(ApiHost::Api.Iam.PermissionParity.FindAudienceProblems(
            [("payment.create", "txn.manage", "dual-console")]));

    [Fact]
    public void An_audience_pair_on_a_single_console_policy_is_flagged() =>
        Assert.Contains(ApiHost::Api.Iam.PermissionParity.FindAudienceProblems(
                [("txn.manage", "payment.create", "admin")]),
            p => p.Contains("dual-console", StringComparison.Ordinal));
}
