extern alias ApiHost;

using Admins.Application;
using Admins.Application.Users;
using BuildingBlocks.Application;
using AdminApprovalWriteAuthorizer = ApiHost::Api.Persistence.AdminApprovalWriteAuthorizer;
using HttpMerchantWriteAuthorizer = ApiHost::Api.Persistence.HttpMerchantWriteAuthorizer;
using MerchantUserAccount = Merchants.Domain.Users.User;
using MerchantRoleAssignment = Merchants.Domain.Users.Roles.RoleAssignment;
using MerchantRegistrationAudit = Merchants.Domain.Users.RegistrationAudit;
using MerchantEntity = Merchants.Domain.Merchant;
using Payments.Domain.Psp;
using Payments.Domain.Routing;

namespace Hosts.Tests;

/// <summary>
/// The admin merchant-control write capability allows only its explicit control-plane write set, confines a
/// non-Empty target merchant to the admin's accessible set, and denies everything else.
/// </summary>
public sealed class AdminApprovalWriteAuthorizerTests
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class StubAdminScope(bool bound, AccessibleMerchants accessible) : IAdminScope
    {
        public bool IsBound => bound;
        public Resolution Current => throw new InvalidOperationException("Not needed by the write floor.");
        public AccessibleMerchants Accessible => accessible;
    }

    [Fact]
    public void The_approve_write_set_is_allowed_inside_the_accessible_scope()
    {
        var floor = new AdminApprovalWriteAuthorizer(
            new StubAdminScope(bound: true, AccessibleMerchants.Of(new HashSet<Guid> { MerchantA })));

        // Approve: User NULL→MerchantA transition + role assignment + audit append.
        Assert.True(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Update, MerchantA));
        Assert.True(floor.CanWrite(typeof(MerchantRoleAssignment), WriteOperation.Insert, MerchantA));
        Assert.True(floor.CanWrite(typeof(MerchantRoleAssignment), WriteOperation.Delete, MerchantA));
        Assert.True(floor.CanWrite(typeof(MerchantRegistrationAudit), WriteOperation.Insert, Guid.Empty));

        // Reject: the target row keeps a NULL tenant key → targetMerchant == Guid.Empty.
        Assert.True(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Update, Guid.Empty));
    }

    [Fact]
    public void A_scoped_admin_is_confined_to_its_accessible_merchants()
    {
        var floor = new AdminApprovalWriteAuthorizer(
            new StubAdminScope(bound: true, AccessibleMerchants.Of(new HashSet<Guid> { MerchantA })));

        Assert.False(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Update, MerchantB));
        Assert.False(floor.CanWrite(typeof(MerchantRoleAssignment), WriteOperation.Insert, MerchantB));
        Assert.False(floor.CanWrite(typeof(MerchantRoleAssignment), WriteOperation.Delete, MerchantB));
    }

    [Fact]
    public void Everything_outside_the_approve_write_set_is_denied()
    {
        var floor = new AdminApprovalWriteAuthorizer(new StubAdminScope(bound: true, AccessibleMerchants.All));

        Assert.False(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Insert, Guid.Empty)); // self-service, not admin
        Assert.False(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Delete, MerchantA));
        Assert.False(floor.CanWrite(typeof(MerchantRoleAssignment), WriteOperation.Update, MerchantA));
        Assert.False(floor.CanWrite(typeof(SharedKernel.Money), WriteOperation.Insert, MerchantA)); // not an owned entity type
        Assert.False(floor.CanWrite(typeof(MerchantRegistrationAudit), WriteOperation.Update, Guid.Empty)); // append-only
    }

    [Fact]
    public void Task4_control_plane_writes_are_allowed_only_inside_the_accessible_scope()
    {
        var floor = new AdminApprovalWriteAuthorizer(
            new StubAdminScope(bound: true, AccessibleMerchants.Of(new HashSet<Guid> { MerchantA })));

        Assert.True(floor.CanWrite(typeof(MerchantEntity), WriteOperation.Update, MerchantA));
        Assert.True(floor.CanWrite(typeof(Merchants.Domain.Originator), WriteOperation.Insert, MerchantA));
        Assert.True(floor.CanWrite(typeof(Connection), WriteOperation.Update, MerchantA));
        Assert.True(floor.CanWrite(typeof(RoutingRuleset), WriteOperation.Insert, MerchantA));
        Assert.True(floor.CanWrite(typeof(RoutingRule), WriteOperation.Delete, MerchantA));

        Assert.False(floor.CanWrite(typeof(MerchantEntity), WriteOperation.Update, MerchantB));
        Assert.False(floor.CanWrite(typeof(Connection), WriteOperation.Insert, MerchantB));
        Assert.False(floor.CanWrite(typeof(RoutingRuleset), WriteOperation.Delete, MerchantB));
    }
}

/// <summary>
/// bugfix-merchant-prebind-wiring T3 (F3, B4): the per-call HTTP selection — an admin-bound request gets the
/// approval capability, every other HTTP request keeps the ordinary merchant-request floor unchanged. The
/// decision is read at CanWrite time, so a context constructed BEFORE authentication binds the admin scope
/// still ends up under the right capability (the HttpActorContext construction-order lesson, defect D3).
/// </summary>
public sealed class HttpMerchantWriteAuthorizerSelectionTests
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class MutableAdminScope : IAdminScope
    {
        public bool IsBound { get; set; }
        public Resolution Current => throw new InvalidOperationException("Not needed by the write floor.");
        public AccessibleMerchants Accessible => AccessibleMerchants.All;
    }

    private sealed class UnboundActor : IActorContext
    {
        public Guid MerchantId => throw new InvalidOperationException("No actor bound.");
        public Guid? UserId => null;
        public bool HasActor => false;
    }

    [Fact]
    public void An_admin_bound_request_gets_the_approval_capability()
    {
        var scope = new MutableAdminScope { IsBound = true };
        var floor = new HttpMerchantWriteAuthorizer(scope, new UnboundActor());

        Assert.True(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Update, MerchantA));
        Assert.False(floor.CanWrite(typeof(SharedKernel.Money), WriteOperation.Insert, Guid.Empty));
    }

    [Fact]
    public void An_unbound_request_keeps_the_ordinary_merchant_floor()
    {
        var scope = new MutableAdminScope { IsBound = false };
        var floor = new HttpMerchantWriteAuthorizer(scope, new UnboundActor());

        Assert.False(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Update, MerchantA)); // D2 boundary intact
        Assert.True(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Insert, Guid.Empty)); // self-service registration
    }

    [Fact]
    public void The_selection_is_read_per_write_not_at_construction()
    {
        var scope = new MutableAdminScope { IsBound = false };
        var floor = new HttpMerchantWriteAuthorizer(scope, new UnboundActor()); // constructed pre-auth

        Assert.False(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Update, MerchantA));

        scope.IsBound = true; // admin session auth finishes binding AFTER the context/floor existed

        Assert.True(floor.CanWrite(typeof(MerchantUserAccount), WriteOperation.Update, MerchantA));
    }
}
