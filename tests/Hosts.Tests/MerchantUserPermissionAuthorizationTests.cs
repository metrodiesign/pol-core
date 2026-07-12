extern alias ApiHost;
using Merchants.Application;
using Merchants.Domain;

namespace Hosts.Tests;

/// <summary>The merchant-user permission gate (REQ-17.2), fail-closed: a bound merchant-user whose effective permission set
/// holds the key is admitted; a missing key or an unbound scope (F10) is denied — never a 500.</summary>
public sealed class MerchantUserPermissionAuthorizationTests
{
    private static ApiHost::Api.MerchantUserScope BoundScope(params string[] permissions)
    {
        var scope = new ApiHost::Api.MerchantUserScope();
        scope.Set(new MerchantUserResolution(Guid.NewGuid(), "p@org.com", Guid.NewGuid(), permissions.ToHashSet(StringComparer.Ordinal)));
        return scope;
    }

    [Fact]
    public void Admits_when_the_effective_set_holds_the_permission() =>
        Assert.True(ApiHost::Api.MerchantUserPermissionAuthorization.IsAllowed(
            BoundScope(MerchantUserPermissions.RolesManage, MerchantUserPermissions.ProductCreate), MerchantUserPermissions.RolesManage));

    [Fact]
    public void Denies_when_the_permission_is_missing() =>
        Assert.False(ApiHost::Api.MerchantUserPermissionAuthorization.IsAllowed(
            BoundScope(MerchantUserPermissions.ProductCreate), MerchantUserPermissions.RolesManage));

    [Fact]
    public void Fails_closed_when_no_merchant_user_is_bound() =>
        Assert.False(ApiHost::Api.MerchantUserPermissionAuthorization.IsAllowed(
            new ApiHost::Api.MerchantUserScope(), MerchantUserPermissions.RolesManage));
}

/// <summary>The boot parity guard (REQ-15.5): a gate key outside the code-canonical catalog is flagged (so the host
/// fails fast); every real catalog key passes.</summary>
public sealed class MerchantUserPermissionParityTests
{
    [Fact]
    public void Every_catalog_key_passes_parity() =>
        Assert.Empty(ApiHost::Api.MerchantUserPermissionParity.FindUnknown(MerchantUserPermissions.AllKeys));

    [Fact]
    public void A_gate_key_outside_the_catalog_is_flagged() =>
        Assert.Contains("bogus.key", ApiHost::Api.MerchantUserPermissionParity.FindUnknown([MerchantUserPermissions.RolesManage, "bogus.key"]));
}
