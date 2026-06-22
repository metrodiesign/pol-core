extern alias ApiHost;

namespace Hosts.Tests;

/// <summary>The tenant-console role gate (REQ-7.3/7.4). A Viewer is denied write/financial actions; an
/// unresolved (null) role passes through to the route's own auth (Development shim / applicant); an unknown
/// role is denied. The gate is wired onto the tenant write endpoints in Program.cs.</summary>
public sealed class TenantRoleAuthorizationTests
{
    private static readonly string[] Writers = ["Finance", "TenantAdmin"];

    private static bool Allowed(string? role) =>
        ApiHost::Api.TenantRoleAuthorization.IsRoleAllowed(role, Writers);

    [Fact] public void Viewer_is_denied_a_write_action() => Assert.False(Allowed("Viewer"));
    [Fact] public void Finance_is_admitted() => Assert.True(Allowed("Finance"));
    [Fact] public void TenantAdmin_is_admitted() => Assert.True(Allowed("TenantAdmin"));
    [Fact] public void An_unresolved_null_role_passes_through() => Assert.True(Allowed(null));
    [Fact] public void An_unknown_role_is_denied() => Assert.False(Allowed("Root"));
}
