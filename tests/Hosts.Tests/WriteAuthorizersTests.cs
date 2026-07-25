extern alias ApiHost;

using Admins.Application;
using Admins.Application.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.DataProtection;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Provisioning;
using BuildingBlocks.Infrastructure.Vault;
using Merchants.Domain;
using Payments.Domain.Psp;
using Persistence.MerchantUsers.Outbox;
using MerchantEntity = Merchants.Domain.Merchant;
using MerchantUser = Merchants.Domain.Users.User;
using AdminUser = Admins.Domain.Users.User;
using AdminAudit = Admins.Domain.Users.Audit;
using AdminSession = Admins.Domain.Users.Session;
using AdminAuthAudit = Admins.Domain.Users.AuthAudit;
using AdminRoleAssignment = Admins.Domain.Roles.RoleAssignment;
using MerchantAccess = Admins.Domain.Users.MerchantAccess;
using Role = Iam.Domain.Roles.Role;
using OrderItem = Orders.Domain.Items.Item;
using OrderItemRevealAudit = Orders.Domain.Items.RevealAudit;
using OrderItemPolicy = Orders.Domain.Items.ItemPolicy;
using OrderItemPolicyAudit = Orders.Domain.Items.ItemPolicyAudit;

namespace Hosts.Tests;

/// <summary>
/// rls-to-query-filter task 8.4: the 3 Api-host <c>IWriteAuthorizer</c> implementations
/// (<c>Api.Persistence.WriteAuthorizers.cs</c>) — each capability's own allow/deny shape, independent of
/// any live DB (these are pure functions over (entityType, operation, targetMerchant) plus a fake actor/scope).
/// The 4th capability, <c>Worker.WorkerWriteAuthorizer</c>, has its own test file
/// (<see cref="WorkerWriteAuthorizerTests"/>) since it lives in a different host assembly (task 8.5.6).
/// </summary>
public sealed class WriteAuthorizersTests
{
    private static readonly Guid MerchantA = Guid.NewGuid();
    private static readonly Guid MerchantB = Guid.NewGuid();

    // --- MerchantRequestWriteAuthorizer ---

    [Fact]
    public void Merchant_request_allows_an_owned_type_with_no_tenant_key_regardless_of_actor()
    {
        var bound = new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(new FakeActor(true, MerchantA));
        var unbound = new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(new FakeActor(false, Guid.Empty));

        Assert.True(bound.CanWrite(typeof(MerchantUser), WriteOperation.Insert, Guid.Empty));
        Assert.True(unbound.CanWrite(typeof(MerchantUser), WriteOperation.Insert, Guid.Empty)); // pre-bind registration
    }

    [Fact]
    public void Merchant_request_allows_own_merchant_and_denies_cross_merchant()
    {
        var authorizer = new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(new FakeActor(true, MerchantA));

        Assert.True(authorizer.CanWrite(typeof(MerchantEntity), WriteOperation.Update, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(MerchantEntity), WriteOperation.Update, MerchantB));
    }

    [Fact]
    public void Merchant_request_denies_a_real_target_merchant_when_the_actor_is_unbound()
    {
        var authorizer = new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(new FakeActor(false, Guid.Empty));

        Assert.False(authorizer.CanWrite(typeof(MerchantEntity), WriteOperation.Insert, MerchantA));
    }

    [Fact]
    public void Merchant_request_allows_the_registration_outbox_sentinel_even_when_unbound()
    {
        var unbound = new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(new FakeActor(false, Guid.Empty));

        Assert.True(unbound.CanWrite(
            typeof(MerchantUserOutbox), WriteOperation.Insert, MerchantRegistrationOutboxSentinel.MerchantId));
    }

    [Fact]
    public void Merchant_request_still_denies_an_arbitrary_non_empty_merchant_when_unbound()
    {
        var unbound = new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(new FakeActor(false, Guid.Empty));

        Assert.False(unbound.CanWrite(typeof(MerchantUserOutbox), WriteOperation.Insert, MerchantA));
    }

    [Fact]
    public void Merchant_request_denies_an_entity_type_outside_the_two_owned_contexts()
    {
        var authorizer = new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(new FakeActor(true, MerchantA));

        Assert.False(authorizer.CanWrite(typeof(AdminUser), WriteOperation.Insert, Guid.Empty));
    }

    // insurance-pivot task 4 — REQ-7.5's detail-read audit write and task 3's own checkout-line insert both
    // go through this authorizer (the Api host), so both new item/audit types need an allowlist entry.
    [Fact]
    public void Merchant_request_allows_OrderItem_and_RevealAudit_for_own_merchant_and_denies_cross_merchant()
    {
        var authorizer = new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(new FakeActor(true, MerchantA));

        Assert.True(authorizer.CanWrite(typeof(OrderItem), WriteOperation.Insert, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(OrderItem), WriteOperation.Insert, MerchantB));

        Assert.True(authorizer.CanWrite(typeof(OrderItemRevealAudit), WriteOperation.Insert, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(OrderItemRevealAudit), WriteOperation.Insert, MerchantB));
    }

    // policy-reference-record task 4 — ItemPolicy's merchant-plane write (create + edit) and its audit trail
    // both go through this authorizer, so both new types need an allowlist entry.
    [Fact]
    public void Merchant_request_allows_ItemPolicy_and_ItemPolicyAudit_for_own_merchant_and_denies_cross_merchant()
    {
        var authorizer = new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(new FakeActor(true, MerchantA));

        Assert.True(authorizer.CanWrite(typeof(OrderItemPolicy), WriteOperation.Insert, MerchantA));
        Assert.True(authorizer.CanWrite(typeof(OrderItemPolicy), WriteOperation.Update, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(OrderItemPolicy), WriteOperation.Update, MerchantB));

        Assert.True(authorizer.CanWrite(typeof(OrderItemPolicyAudit), WriteOperation.Insert, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(OrderItemPolicyAudit), WriteOperation.Insert, MerchantB));
    }

    // --- ProvisioningSuperWriteAuthorizer ---

    [Theory]
    [InlineData(typeof(MerchantEntity), WriteOperation.Insert)]
    [InlineData(typeof(Connection), WriteOperation.Insert)]
    [InlineData(typeof(VaultSecretBlob), WriteOperation.Insert)]
    [InlineData(typeof(ProvisioningAudit), WriteOperation.Insert)]
    [InlineData(typeof(ProvisioningOperation), WriteOperation.Update)]
    public void Provisioning_super_allows_exactly_the_coordinators_own_writes(Type entityType, WriteOperation op)
    {
        var authorizer = new ApiHost::Api.Persistence.ProvisioningSuperWriteAuthorizer();

        Assert.True(authorizer.CanWrite(entityType, op, Guid.Empty));
    }

    [Fact]
    public void Provisioning_super_denies_the_same_type_under_a_different_operation()
    {
        var authorizer = new ApiHost::Api.Persistence.ProvisioningSuperWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(MerchantEntity), WriteOperation.Update, Guid.Empty));
        Assert.False(authorizer.CanWrite(typeof(ProvisioningOperation), WriteOperation.Insert, Guid.Empty));
    }

    [Fact]
    public void Provisioning_super_denies_an_entity_type_outside_its_narrow_set()
    {
        var authorizer = new ApiHost::Api.Persistence.ProvisioningSuperWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(MerchantUser), WriteOperation.Insert, Guid.Empty));
    }

    // --- ControlPlaneAdminWriteAuthorizer ---

    [Fact]
    public void Control_plane_admin_allows_an_owned_type_when_bound()
    {
        var bound = new ApiHost::Api.Persistence.ControlPlaneAdminWriteAuthorizer(new FakeScope(true));

        Assert.True(bound.CanWrite(typeof(AdminUser), WriteOperation.Update, Guid.Empty));
        Assert.True(bound.CanWrite(typeof(Role), WriteOperation.Insert, Guid.Empty));
    }

    // Bugfix: every write the OIDC callback makes (bootstrap self-provision, invite-bind, session start, the
    // login-success AND denied-auth audits) happens BEFORE any admin scope exists — the scope binds from the
    // session cookie the callback is busy creating. Gating those on IsBound bricked the whole admin login.
    [Theory]
    [InlineData(typeof(AdminUser), WriteOperation.Insert)]           // allowlist bootstrap
    [InlineData(typeof(AdminUser), WriteOperation.Update)]           // invite-bind stamps the subject
    [InlineData(typeof(AdminAudit), WriteOperation.Insert)]          // self-provision/bind audit
    [InlineData(typeof(AdminRoleAssignment), WriteOperation.Insert)] // bootstrap platform_admin role
    [InlineData(typeof(AdminSession), WriteOperation.Insert)]        // session start at callback
    [InlineData(typeof(AdminAuthAudit), WriteOperation.Insert)]      // login-success/denied audit
    public void Control_plane_admin_unbound_allows_exactly_the_callback_login_writes(Type entity, WriteOperation op)
    {
        var unbound = new ApiHost::Api.Persistence.ControlPlaneAdminWriteAuthorizer(new FakeScope(false));

        Assert.True(unbound.CanWrite(entity, op, Guid.Empty));
    }

    [Theory]
    [InlineData(typeof(AdminUser), WriteOperation.Delete)]           // no unbound deletes
    [InlineData(typeof(AdminSession), WriteOperation.Update)]        // rotation/revoke = bound requests
    [InlineData(typeof(AdminAuthAudit), WriteOperation.Update)]      // audits are append-only pre-bind
    [InlineData(typeof(Role), WriteOperation.Insert)]                // role catalog stays bound-only
    [InlineData(typeof(MerchantAccess), WriteOperation.Insert)]      // assignments stay bound-only
    public void Control_plane_admin_unbound_denies_everything_outside_the_login_flow(Type entity, WriteOperation op)
    {
        var unbound = new ApiHost::Api.Persistence.ControlPlaneAdminWriteAuthorizer(new FakeScope(false));

        Assert.False(unbound.CanWrite(entity, op, Guid.Empty));
    }

    [Fact]
    public void Control_plane_admin_allows_data_protection_keys_unconditionally()
    {
        var unbound = new ApiHost::Api.Persistence.ControlPlaneAdminWriteAuthorizer(new FakeScope(false));

        Assert.True(unbound.CanWrite(typeof(DataProtectionKey), WriteOperation.Insert, Guid.Empty));
    }

    [Fact]
    public void Control_plane_admin_never_allows_the_provisioning_ledger_even_when_bound()
    {
        var bound = new ApiHost::Api.Persistence.ControlPlaneAdminWriteAuthorizer(new FakeScope(true));

        Assert.False(bound.CanWrite(typeof(ProvisioningOperation), WriteOperation.Update, Guid.Empty));
    }

    // --- AdminItemPolicyWriteAuthorizer (policy-reference-record task 5) ---

    [Fact]
    public void Admin_item_policy_super_allows_any_merchant()
    {
        var authorizer = new ApiHost::Api.Persistence.AdminItemPolicyWriteAuthorizer(new FakeAdminScope(AccessibleMerchants.All));

        Assert.True(authorizer.CanWrite(typeof(OrderItemPolicy), WriteOperation.Insert, MerchantA));
        Assert.True(authorizer.CanWrite(typeof(OrderItemPolicy), WriteOperation.Update, MerchantB));
        Assert.True(authorizer.CanWrite(typeof(OrderItemPolicyAudit), WriteOperation.Insert, MerchantB));
    }

    // The security boundary this task exists for: a Scoped admin must NOT be able to write outside its own
    // assigned merchants, even though the (type, operation) pair is otherwise allowed.
    [Fact]
    public void Admin_item_policy_scoped_allows_only_its_accessible_merchants()
    {
        var authorizer = new ApiHost::Api.Persistence.AdminItemPolicyWriteAuthorizer(
            new FakeAdminScope(AccessibleMerchants.Of(new HashSet<Guid> { MerchantA })));

        Assert.True(authorizer.CanWrite(typeof(OrderItemPolicy), WriteOperation.Update, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(OrderItemPolicy), WriteOperation.Update, MerchantB));
        Assert.False(authorizer.CanWrite(typeof(OrderItemPolicyAudit), WriteOperation.Insert, MerchantB));
    }

    [Fact]
    public void Admin_item_policy_denies_delete_even_for_a_super_admin()
    {
        var authorizer = new ApiHost::Api.Persistence.AdminItemPolicyWriteAuthorizer(new FakeAdminScope(AccessibleMerchants.All));

        Assert.False(authorizer.CanWrite(typeof(OrderItemPolicy), WriteOperation.Delete, MerchantA));
    }

    [Fact]
    public void Admin_item_policy_denies_an_entity_type_outside_its_narrow_set()
    {
        var authorizer = new ApiHost::Api.Persistence.AdminItemPolicyWriteAuthorizer(new FakeAdminScope(AccessibleMerchants.All));

        Assert.False(authorizer.CanWrite(typeof(MerchantEntity), WriteOperation.Update, MerchantA));
    }

    private sealed class FakeActor(bool hasActor, Guid merchantId) : IActorContext
    {
        public Guid MerchantId => hasActor ? merchantId : throw new InvalidOperationException("No actor bound.");
        public Guid? UserId => null;
        public bool HasActor => hasActor;
    }

    private sealed class FakeScope(bool isBound) : IAdminScope
    {
        public bool IsBound => isBound;
        public Resolution Current => throw new NotSupportedException();
        public AccessibleMerchants Accessible => throw new NotSupportedException();
    }

    // Unlike FakeScope above (whose Accessible is never read by ControlPlaneAdminWriteAuthorizer, only
    // IsBound), AdminItemPolicyWriteAuthorizer reads Accessible unconditionally — this fake carries a real one.
    private sealed class FakeAdminScope(AccessibleMerchants accessible) : IAdminScope
    {
        public bool IsBound => true;
        public Resolution Current => throw new NotSupportedException();
        public AccessibleMerchants Accessible => accessible;
    }
}
