using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.DataProtection;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Provisioning;
using BuildingBlocks.Infrastructure.Vault;
using Persistence.MerchantUsers.Outbox;
using Admins.Application;
using AdminRoleAssignment = Admins.Domain.Roles.RoleAssignment;
using AdminUser = Admins.Domain.Users.User;
using AdminAudit = Admins.Domain.Users.Audit;
using AdminSession = Admins.Domain.Users.Session;
using AdminAuthAudit = Admins.Domain.Users.AuthAudit;
using MerchantAccess = Admins.Domain.Users.MerchantAccess;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Divisions.Domain;
using Levels.Domain;
using Offices.Domain;
using Positions.Domain;
using Merchants.Domain;
using MerchantEntity = Merchants.Domain.Merchant;
using MerchantUser = Merchants.Domain.Users.User;
using MerchantSession = Merchants.Domain.Users.Session;
using MerchantExternalLogin = Merchants.Domain.Users.ExternalLogin;
using MerchantAuthAudit = Merchants.Domain.Users.AuthAudit;
using MerchantRegistrationAudit = Merchants.Domain.Users.RegistrationAudit;
using MerchantRegistrationAttempt = Merchants.Domain.Users.RegistrationAttempt;
using MerchantRegistrationNotice = Merchants.Domain.Users.RegistrationNotice;
using MerchantRoleAssignment = Merchants.Domain.Users.Roles.RoleAssignment;
using Payments.Domain.Psp;
using CartAggregate = Carts.Domain.Cart;
using CartItem = Carts.Domain.Items.Item;
using CheckoutSession = Checkouts.Domain.Session;
using CheckoutSessionItem = Checkouts.Domain.Items.Item;
using PaymentSession = Payments.Domain.Session;
using OrderAggregate = Orders.Domain.Order;
using OrderItem = Orders.Domain.Items.Item;
using OrderItemRevealAudit = Orders.Domain.Items.RevealAudit;
using OrderItemPolicy = Orders.Domain.Items.ItemPolicy;
using OrderItemPolicyAudit = Orders.Domain.Items.ItemPolicyAudit;

namespace Api.Persistence;

/// <summary>
/// The 3 Api-host <see cref="IWriteAuthorizer"/> implementations (rls-to-query-filter task 8.4,
/// design.md/task 3's own doc comment on <see cref="IWriteAuthorizer"/>: "production registrations differ
/// per flow"). Each is a small, host-owned class bridging the framework write-floor port to the specific
/// capability that constructs a runtime context — matches <c>HostRoleAssignmentCounter</c>'s existing
/// precedent (<c>Api/Iam/RoleHostWiring.cs</c>) for a host-layer class no Persistence assembly can express
/// itself (it would need to reference every module's domain types). Registered per-context-factory (task
/// 8.5), never as one global DI registration — a merchant request's authorizer is a DIFFERENT instance than
/// the provisioning coordinator's, even though both may construct the same context CLASS.
/// <para>
/// The 4th capability, <c>WorkerWriteAuthorizer</c>, lives in <c>Hosts/Worker</c> instead (task 8.5.6) — it
/// is stateless (zero dependencies) and the ONLY capability Worker needs, so giving Worker a ProjectReference
/// to this whole Api host just to reach one small class would pull Worker's process dependency graph in
/// (ASP.NET Core MVC/Minimal API surface, OIDC, Scalar, ...) for no reason. Each host owns its own
/// authorizer(s); nothing forces them into one assembly.
/// </para>
/// </summary>

/// <summary>
/// Merchant-request capability: constructs <c>MerchantUserDbContext</c>/<c>MerchantRuntimeDbContext</c> per
/// HTTP request. Allows a write when the entity type is one of the two contexts' own (an entity type from
/// ANY other capability's set can never reach these two contexts in practice — internal sealed + no other
/// factory constructs them with this authorizer — but the allowlist is the actual enforcement, not an
/// assumption) AND either: the entity carries no tenant key of its own (targetMerchant is
/// <see cref="Guid.Empty"/> — <see cref="GuardedRuntimeDbContext"/> only ever passes Empty here for a
/// genuinely keyless entity type OR a tenant-keyed entity whose nullable key is still NULL, e.g. an
/// anonymous pre-bind registration row, REQ-11.7 — the write-floor already rejected a REAL Empty tenant key
/// before this method is ever called), or the entity's own merchant matches the bound actor's. An unbound
/// actor can only ever satisfy the first branch (accessing <c>IActorContext.MerchantId</c> while unbound
/// throws, so <see cref="IActorContext.HasActor"/> is checked first) — this is what makes task 5's pre-bind
/// write ports (anonymous registration submission, approve/reject before a session exists) work under this
/// same authorizer with no special case.
/// <para>
/// ONE further carve-out (task 8.5.7): <see cref="MerchantRegistrationOutboxSentinel.MerchantId"/> is also
/// allowed unconditionally. Unlike Empty, <c>MerchantUserOutbox.MerchantId</c> is a non-nullable tenant key
/// (the write floor rejects Empty outright for it), so a still-pending registration's outbox row needs a
/// real, non-Empty placeholder — the sentinel. This is the ONE non-Empty value the write floor lets an
/// unbound actor's anonymous registration submission write; every other non-Empty value still requires a
/// matching bound actor.
/// </para>
/// </summary>
internal sealed class MerchantRequestWriteAuthorizer : IWriteAuthorizer
{
    private static readonly HashSet<Type> OwnedTypes =
    [
        // MerchantUserDbContext
        typeof(MerchantUser), typeof(MerchantSession), typeof(MerchantExternalLogin), typeof(MerchantAuthAudit),
        typeof(MerchantRegistrationAudit), typeof(MerchantRegistrationAttempt), typeof(MerchantRegistrationNotice),
        typeof(MerchantRoleAssignment),
        typeof(MerchantUserOutbox),
        // MerchantRuntimeDbContext
        typeof(CartAggregate), typeof(CartItem), typeof(CheckoutSession), typeof(CheckoutSessionItem),
        typeof(OrderAggregate), typeof(OrderItem), typeof(OrderItemRevealAudit),
        typeof(OrderItemPolicy), typeof(OrderItemPolicyAudit),
        typeof(PaymentSession), typeof(Connection), typeof(IdempotencyRecord), typeof(OutboxMessage),
        typeof(MerchantEntity), typeof(VaultSecretBlob), typeof(VaultRevealAudit), typeof(ProvisioningAudit),
    ];

    private readonly IActorContext _actor;

    public MerchantRequestWriteAuthorizer(IActorContext actor) => _actor = actor;

    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant)
    {
        if (!OwnedTypes.Contains(entityType))
            return false;
        if (targetMerchant == Guid.Empty)
            return true;
        if (targetMerchant == MerchantRegistrationOutboxSentinel.MerchantId)
            return true;
        return _actor.HasActor && targetMerchant == _actor.MerchantId;
    }
}

/// <summary>
/// Admin approval capability over <c>MerchantUserDbContext</c> (bugfix-merchant-prebind-wiring F3/F4): an
/// admin-plane request has no bound merchant actor, so <see cref="MerchantRequestWriteAuthorizer"/> denies
/// the approve write set unconditionally — the exact failure class PR #124 fixed on the control plane and
/// <c>AdminItemPolicyWriteAuthorizer</c> already solves for ItemPolicy. Allows ONLY what approve/reject stage:
/// the target user row's update (approve performs the one-time NULL→merchant tenant-key transition; reject
/// leaves it NULL → <c>targetMerchant == Guid.Empty</c>), the approval's role-assignment insert, and the
/// registration-audit append. A non-Empty target must sit inside the admin's accessible-merchant set — Scoped
/// admins stay confined (same accessible-set floor as the host's pre-dispatch check), Supers are unrestricted.
/// </summary>
internal sealed class AdminApprovalWriteAuthorizer : IWriteAuthorizer
{
    private static readonly HashSet<(Type, WriteOperation)> Allowed =
    [
        (typeof(MerchantUser), WriteOperation.Update),
        (typeof(MerchantRoleAssignment), WriteOperation.Insert),
        (typeof(MerchantRegistrationAudit), WriteOperation.Insert),
    ];

    private readonly IAdminScope _scope;

    public AdminApprovalWriteAuthorizer(IAdminScope scope) => _scope = scope;

    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
        Allowed.Contains((entityType, operation))
        && (targetMerchant == Guid.Empty || _scope.Accessible.Allows(targetMerchant));
}

/// <summary>
/// The per-call HTTP selection between the two request-plane capabilities above
/// (bugfix-merchant-prebind-wiring F3): an admin-bound request (admin session auth ran → <c>IAdminScope</c>
/// bound) gets <see cref="AdminApprovalWriteAuthorizer"/>; every other HTTP request keeps the ordinary
/// <see cref="MerchantRequestWriteAuthorizer"/>. Decided PER WRITE, not at context construction — a
/// scoped <c>MerchantUserDbContext</c> may be constructed before authentication finishes binding the scope
/// (the same construction-order hazard as <c>HttpActorContext</c>, defect D3), so the selection must read
/// <c>IAdminScope.IsBound</c> at CanWrite time.
/// </summary>
internal sealed class HttpMerchantWriteAuthorizer : IWriteAuthorizer
{
    private readonly IAdminScope _adminScope;
    private readonly AdminApprovalWriteAuthorizer _admin;
    private readonly MerchantRequestWriteAuthorizer _merchant;

    public HttpMerchantWriteAuthorizer(IAdminScope adminScope, IActorContext actor)
    {
        _adminScope = adminScope;
        _admin = new AdminApprovalWriteAuthorizer(adminScope);
        _merchant = new MerchantRequestWriteAuthorizer(actor);
    }

    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
        _adminScope.IsBound
            ? _admin.CanWrite(entityType, operation, targetMerchant)
            : _merchant.CanWrite(entityType, operation, targetMerchant);
}

/// <summary>
/// Provisioning-Super capability: constructed ONLY inside <c>ProvisioningCoordinator</c>'s two context
/// factories (design.md "Provisioning UoW"). A narrow, hardcoded (entityType, operation) allowlist — NOT
/// actor-based, because the coordinator has already verified the caller is an active Super admin at the
/// expected authorization version via its own in-transaction raw-SQL check (step 3) before any tracked
/// write happens. Exactly the entity set the coordinator's own <c>AttemptAsync</c> touches: 4 inserts
/// (Merchant, PspConnection, VaultSecretBlob, ProvisioningAudit — all keyed off the one pre-minted
/// <c>MerchantId</c>) plus one targeted Update (the ledger row's Result column, via
/// <c>Attach</c>+<c>IsModified</c> — the INSERT itself is a raw parameterized SQL statement that never
/// touches the change tracker, so it never reaches this authorizer at all).
/// </summary>
internal sealed class ProvisioningSuperWriteAuthorizer : IWriteAuthorizer
{
    private static readonly HashSet<(Type, WriteOperation)> Allowed =
    [
        (typeof(MerchantEntity), WriteOperation.Insert),
        (typeof(Connection), WriteOperation.Insert),
        (typeof(VaultSecretBlob), WriteOperation.Insert),
        (typeof(ProvisioningAudit), WriteOperation.Insert),
        (typeof(ProvisioningOperation), WriteOperation.Update),
    ];

    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
        Allowed.Contains((entityType, operation));
}

/// <summary>
/// ControlPlaneDbContext admin capability: constructed for ordinary admin-console requests. Allows a write
/// on the context's own entity types when <see cref="IAdminScope.IsBound"/> — <c>targetMerchant</c> is
/// always <see cref="Guid.Empty"/> for every ControlPlaneDbContext entity (no tenant key of its own), so the
/// decision rests on bound-or-not + entity-type allowlist alone. TWO exclusions from the allowlist,
/// both deliberate:
/// <list type="bullet">
/// <item><see cref="ProvisioningOperation"/> — its ONLY sanctioned writer is
/// <see cref="ProvisioningSuperWriteAuthorizer"/> (a DIFFERENT authorizer instance bound to a DIFFERENT
/// ControlPlaneDbContext instance, the provisioning coordinator's own); including it here would let an
/// ordinary admin request tracked-write the idempotency ledger the coordinator's raw-SQL-insert +
/// narrow-attach pattern depends on staying untouched by anything else.</item>
/// <item><see cref="DataProtectionKey"/> — allowed UNCONDITIONALLY (not gated on <c>IsBound</c>): ASP.NET
/// Core's Data Protection key ring reads/writes on its own background schedule, not as part of a
/// bound-admin HTTP request (design.md R1-v7 #9 — the XmlRepository moves into this assembly, task 8.5's
/// DI wiring).</item>
/// </list>
/// <para>
/// UNBOUND carve-out (bugfix, mirrors the merchant authorizer's pre-bind branch): every write the OIDC
/// callback makes happens BEFORE any admin scope exists — the scope binds from the session cookie on the
/// NEXT request, and the callback is the request that CREATES the session. Gating those on
/// <c>IsBound</c> bricked the whole admin login (bootstrap self-provision, invite-bind, session start,
/// login-success audit — and even the denied-auth audit, so the failure was invisible in
/// <c>admin.AuthAudits</c>). An unbound actor gets exactly the narrow (entity, operation) set the login
/// flow needs; everything else still requires a bound scope.
/// </para>
/// </summary>
internal sealed class ControlPlaneAdminWriteAuthorizer : IWriteAuthorizer
{
    private static readonly HashSet<Type> BoundOnlyTypes =
    [
        typeof(AdminUser), typeof(MerchantAccess), typeof(AdminAudit), typeof(AdminSession), typeof(AdminAuthAudit),
        typeof(AdminRoleAssignment),
        typeof(Role), typeof(RolePermission), typeof(PermissionGroup), typeof(Permission),
        typeof(Position), typeof(Office), typeof(Level), typeof(Division),
    ];

    // The callback-time login flow, and nothing else: allowlist bootstrap (User+Audit+RoleAssignment),
    // invite-bind (User update + Audit), session start (Session), and the login-success/denied audits
    // (AuthAudit). No unbound Delete, and no unbound access to roles/permissions/master data.
    // The TRUST ROOTS for these writes live in the REQ-9.5 pre-bind write ports (bootstrap Subject
    // allowlist / invited-email lookup / the verified OIDC principal itself) — this floor is the
    // defense-in-depth layer under them, (type, op)-narrow by design, mirroring
    // MerchantRequestWriteAuthorizer's accepted pre-bind branch above.
    private static readonly HashSet<(Type, WriteOperation)> UnboundLoginFlowWrites =
    [
        (typeof(AdminUser), WriteOperation.Insert),
        (typeof(AdminUser), WriteOperation.Update),
        (typeof(AdminAudit), WriteOperation.Insert),
        (typeof(AdminRoleAssignment), WriteOperation.Insert),
        (typeof(AdminSession), WriteOperation.Insert),
        (typeof(AdminAuthAudit), WriteOperation.Insert),
    ];

    private readonly IAdminScope _scope;

    public ControlPlaneAdminWriteAuthorizer(IAdminScope scope) => _scope = scope;

    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant)
    {
        if (entityType == typeof(DataProtectionKey))
            return true;
        if (_scope.IsBound)
            return BoundOnlyTypes.Contains(entityType);
        return UnboundLoginFlowWrites.Contains((entityType, operation));
    }
}

/// <summary>
/// Admin cross-merchant write capability for <see cref="OrderItemPolicy"/>/<see cref="OrderItemPolicyAudit"/>
/// (policy-reference-record REQ-3.2-admin/3.3-admin, design.md "Write guard registration" §Admin plane point
/// 2) — constructed ONLY for the dedicated <c>IAdminItemPolicyWriter</c> context
/// (<c>Persistence.MerchantRuntime.AddAdminItemPolicyWriter</c>, mirror
/// <see cref="ProvisioningSuperWriteAuthorizer"/>'s per-capability construction). Unlike that Super-only
/// allowlist, this one is NOT unconditional for the allowed (type, operation) pairs — it also checks the
/// CALLER's accessible-merchant set, because a Scoped admin (not just Super) may reach this port and must be
/// confined to its own assigned merchants. <c>AccessibleMerchants.Allows</c> already folds in the
/// Super/unrestricted case, so no separate IsUnrestricted branch is needed here.
/// </summary>
internal sealed class AdminItemPolicyWriteAuthorizer : IWriteAuthorizer
{
    private static readonly HashSet<(Type, WriteOperation)> Allowed =
    [
        (typeof(OrderItemPolicy), WriteOperation.Insert),
        (typeof(OrderItemPolicy), WriteOperation.Update),
        (typeof(OrderItemPolicyAudit), WriteOperation.Insert),
    ];

    private readonly IAdminScope _scope;

    public AdminItemPolicyWriteAuthorizer(IAdminScope scope) => _scope = scope;

    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
        Allowed.Contains((entityType, operation)) && _scope.Accessible.Allows(targetMerchant);
}
