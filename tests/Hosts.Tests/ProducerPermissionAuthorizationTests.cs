extern alias ApiHost;
using Producer.Application;
using Producer.Domain;

namespace Hosts.Tests;

/// <summary>The producer permission gate (REQ-17.2), fail-closed: a bound producer whose effective permission set
/// holds the key is admitted; a missing key or an unbound scope (a tenant-Bearer caller, F10) is denied — never a
/// 500.</summary>
public sealed class ProducerPermissionAuthorizationTests
{
    private static ApiHost::Api.ProducerScope BoundScope(params string[] permissions)
    {
        var scope = new ApiHost::Api.ProducerScope();
        scope.Set(new ProducerResolution(Guid.NewGuid(), "p@org.com", Guid.NewGuid(), permissions.ToHashSet(StringComparer.Ordinal)));
        return scope;
    }

    [Fact]
    public void Admits_when_the_effective_set_holds_the_permission() =>
        Assert.True(ApiHost::Api.ProducerPermissionAuthorization.IsAllowed(
            BoundScope(ProducerPermissions.RolesManage, ProducerPermissions.ProductCreate), ProducerPermissions.RolesManage));

    [Fact]
    public void Denies_when_the_permission_is_missing() =>
        Assert.False(ApiHost::Api.ProducerPermissionAuthorization.IsAllowed(
            BoundScope(ProducerPermissions.ProductCreate), ProducerPermissions.RolesManage));

    [Fact]
    public void Fails_closed_when_no_producer_is_bound() =>
        Assert.False(ApiHost::Api.ProducerPermissionAuthorization.IsAllowed(
            new ApiHost::Api.ProducerScope(), ProducerPermissions.RolesManage));
}

/// <summary>The boot parity guard (REQ-15.5): a gate key outside the code-canonical catalog is flagged (so the host
/// fails fast); every real catalog key passes.</summary>
public sealed class ProducerPermissionParityTests
{
    [Fact]
    public void Every_catalog_key_passes_parity() =>
        Assert.Empty(ApiHost::Api.ProducerPermissionParity.FindUnknown(ProducerPermissions.AllKeys));

    [Fact]
    public void A_gate_key_outside_the_catalog_is_flagged() =>
        Assert.Contains("bogus.key", ApiHost::Api.ProducerPermissionParity.FindUnknown([ProducerPermissions.RolesManage, "bogus.key"]));
}
