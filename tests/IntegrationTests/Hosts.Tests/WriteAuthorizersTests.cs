extern alias ApiHost;
using Admins.Application;
using Admins.Application.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.DataProtection;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Provisioning;
using BuildingBlocks.Infrastructure.Vault;
using Merchants.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;
using Persistence.MerchantUsers.Outbox;
using MerchantEntity = Merchants.Domain.Merchant;
using MerchantUser = Merchants.Domain.Users.User;
using AdminAudit = Admins.Domain.Users.Audit;
using Role = Iam.Domain.Roles.Role;
using OrderItem = Orders.Domain.Items.Item;
using OrderItemRevealAudit = Orders.Domain.Items.RevealAudit;

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

        Assert.False(authorizer.CanWrite(typeof(AdminAudit), WriteOperation.Insert, Guid.Empty));
    }

    // Direct Order creation and detail-read audit both use merchant request write floor.
    [Fact]
    public void Merchant_request_allows_OrderItem_and_RevealAudit_for_own_merchant_and_denies_cross_merchant()
    {
        var authorizer = new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(new FakeActor(true, MerchantA));

        Assert.True(authorizer.CanWrite(typeof(OrderItem), WriteOperation.Insert, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(OrderItem), WriteOperation.Insert, MerchantB));

        Assert.True(authorizer.CanWrite(typeof(OrderItemRevealAudit), WriteOperation.Insert, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(OrderItemRevealAudit), WriteOperation.Insert, MerchantB));
    }

    // --- ProvisioningSuperWriteAuthorizer ---

    [Theory]
    [InlineData(typeof(MerchantEntity), WriteOperation.Insert)]
    [InlineData(typeof(Connection), WriteOperation.Insert)]
    [InlineData(typeof(MerchantProviderAccountMethod), WriteOperation.Insert)]
    [InlineData(typeof(MerchantPaymentMethod), WriteOperation.Insert)]
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

        Assert.True(bound.CanWrite(typeof(AdminAudit), WriteOperation.Insert, Guid.Empty));
        Assert.True(bound.CanWrite(typeof(Role), WriteOperation.Insert, Guid.Empty));
    }

    // The employee login flow's admin audit is the only unbound write the ControlPlane admin authorizer still
    // allows: the legacy admin identity plane (self-provision/invite-bind/role bootstrap over admin.Users) was
    // retired, so those unbound writes are gone with it.
    [Theory]
    [InlineData(typeof(AdminAudit), WriteOperation.Insert)]          // login-success/denied audit
    public void Control_plane_admin_unbound_allows_exactly_the_callback_login_writes(Type entity, WriteOperation op)
    {
        var unbound = new ApiHost::Api.Persistence.ControlPlaneAdminWriteAuthorizer(new FakeScope(false));

        Assert.True(unbound.CanWrite(entity, op, Guid.Empty));
    }

    [Theory]
    [InlineData(typeof(AdminAudit), WriteOperation.Delete)]          // no unbound deletes
    [InlineData(typeof(Role), WriteOperation.Insert)]                // role catalog stays bound-only
    public void Control_plane_admin_unbound_denies_everything_outside_the_login_flow(Type entity, WriteOperation op)
    {
        var unbound = new ApiHost::Api.Persistence.ControlPlaneAdminWriteAuthorizer(new FakeScope(false));

        Assert.False(unbound.CanWrite(entity, op, Guid.Empty));
    }

    // The OIDC callback runs before any admin scope exists: a repeat login observes the provider's current
    // email/display name on the existing LoginAccount, and the OpenIddict token/authorization rows of the
    // employee login are written unbound as well.
    [Theory]
    [InlineData(typeof(Accounts.Domain.LoginAccount), WriteOperation.Update)]
    [InlineData(typeof(OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken), WriteOperation.Insert)]
    [InlineData(typeof(OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization), WriteOperation.Update)]
    public void Control_plane_admin_unbound_allows_the_employee_login_lifecycle_writes(Type entity, WriteOperation op)
    {
        var unbound = new ApiHost::Api.Persistence.ControlPlaneAdminWriteAuthorizer(new FakeScope(false));

        Assert.True(unbound.CanWrite(entity, op, Guid.Empty));
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

}
