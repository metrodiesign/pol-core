extern alias ApiHost;

namespace Hosts.Tests;

/// <summary>The CSRF boot parity guard: every endpoint that accepts an unsafe method under a cookie-session
/// policy must carry the <c>CsrfProtected</c> marker of its own scheme (attached by
/// <c>RequireCsrf()</c>/<c>RequireUserCsrf()</c> alongside the filter — a bare <c>AddEndpointFilter</c> leaves
/// no metadata). Endpoints with no recognized policy (PSP webhooks, anonymous pre-session routes) are skipped.
/// The boot-time inventory itself is exercised for free by every WebApplicationFactory test, which runs
/// <c>CsrfParity.Assert</c> against the real endpoint table.</summary>
public sealed class CsrfParityTests
{
    private static IReadOnlyList<string> Find(
        params (string Pattern, IReadOnlyList<string>? Methods, string? Policy, IReadOnlyList<string> Schemes)[] endpoints) =>
        ApiHost::Api.Iam.CsrfParity.FindProblems(endpoints);

    [Fact]
    public void An_unsafe_merchant_user_endpoint_without_the_marker_is_flagged() =>
        Assert.Contains(Find(("/api/v1/carts", ["POST"], "merchant-user", [])),
            p => p.Contains("no MerchantUserSession", StringComparison.Ordinal));

    [Fact]
    public void An_admin_endpoint_carrying_the_merchant_side_filter_is_flagged_as_wrong_side() =>
        Assert.Contains(Find(("/api/v1/admins", ["POST"], "admin", ["MerchantUserSession"])),
            p => p.Contains("wrong side", StringComparison.Ordinal));

    [Fact]
    public void A_safe_only_endpoint_without_the_marker_passes() =>
        Assert.Empty(Find(("/api/v1/orders", ["GET"], "merchant-user", [])));

    [Fact]
    public void An_unsafe_endpoint_with_no_recognized_policy_is_skipped() =>
        // PSP webhooks and anonymous pre-session routes have no cookie session to forge.
        Assert.Empty(Find(("/api/v1/webhooks/{pspConnectionId:guid}", ["POST"], null, [])));

    [Fact]
    public void Missing_method_metadata_counts_as_unsafe() =>
        Assert.Contains(Find(("/api/v1/admins/something", null, "admin", [])),
            p => p.Contains("no AdminSession", StringComparison.Ordinal));

    [Fact]
    public void A_safe_endpoint_carrying_its_own_sides_marker_passes() =>
        // GET /merchants/{code} attaches the filter deliberately (REQ-7.1) — a marker on a safe route is fine.
        Assert.Empty(Find(("/api/v1/merchants/{code}", ["GET"], "admin", ["AdminSession"])));

    [Fact]
    public void An_unsafe_endpoint_with_its_own_sides_marker_passes() =>
        Assert.Empty(Find(("/api/v1/carts", ["POST"], "merchant-user", ["MerchantUserSession"])));

    [Fact]
    public void An_unsafe_dual_console_endpoint_requires_both_audience_markers() =>
        Assert.Contains(Find(("/api/v1/carts", ["POST"], "dual-console", ["AdminSession"])),
            p => p.Contains("no MerchantUserSession", StringComparison.Ordinal));

    [Fact]
    public void An_unsafe_dual_console_endpoint_with_both_markers_passes() =>
        Assert.Empty(Find(("/api/v1/carts", ["POST"], "dual-console",
            ["AdminSession", "MerchantUserSession"])));
}
