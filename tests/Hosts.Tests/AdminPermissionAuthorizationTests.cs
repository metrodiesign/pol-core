extern alias ApiHost;
using Admin.Application;
using Admin.Application.ResolveAdmin;
using Admin.Domain;

namespace Hosts.Tests;

/// <summary>The role permission gate (admin-role-rbac REQ-6.1/6.2), fail-closed: a bound admin whose effective
/// permission set holds the key is admitted; a missing key or an unbound scope (S4) is denied — never a 500.</summary>
public sealed class AdminPermissionAuthorizationTests
{
    private static ApiHost::Api.AdminScope BoundScope(params string[] permissions)
    {
        var scope = new ApiHost::Api.AdminScope();
        scope.Set(new AdminResolution(Guid.NewGuid(), "a@org.com", PlatformUserTier.Scoped, AccessibleMerchants.All)
        {
            Permissions = permissions.ToHashSet(),
        });
        return scope;
    }

    [Fact]
    public void Admits_when_the_effective_set_holds_the_permission() =>
        Assert.True(ApiHost::Api.AdminPermissionAuthorization.IsAllowed(BoundScope("user.roles", "txn.view"), "user.roles"));

    [Fact]
    public void Denies_when_the_permission_is_missing() =>
        Assert.False(ApiHost::Api.AdminPermissionAuthorization.IsAllowed(BoundScope("txn.view"), "user.roles"));

    [Fact]
    public void Fails_closed_when_no_admin_is_bound() =>
        Assert.False(ApiHost::Api.AdminPermissionAuthorization.IsAllowed(new ApiHost::Api.AdminScope(), "user.roles"));
}

/// <summary>The boot parity guard (admin-role-rbac REQ-11): a gate key outside the code-canonical catalog is
/// flagged (so the host fails fast); every real catalog key passes.</summary>
public sealed class AdminPermissionParityTests
{
    [Fact]
    public void Every_catalog_key_passes_parity() =>
        Assert.Empty(ApiHost::Api.AdminPermissionParity.FindUnknown(AdminPermissions.AllKeys));

    [Fact]
    public void A_gate_key_outside_the_catalog_is_flagged() =>
        Assert.Contains("bogus.key", ApiHost::Api.AdminPermissionParity.FindUnknown(["user.roles", "bogus.key"]));
}
