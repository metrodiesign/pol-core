using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;

namespace Merchants.Tests;

/// <summary>Merchant real API REQ-9.5/9.14/9.18/9.22: invitation safety, lifecycle audit,
/// tenant isolation, and last-manager protection.</summary>
public sealed class MerchantUserManagementTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 1, 0, 0, DateTimeKind.Utc);
    private static readonly Guid MerchantId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ActorId = Guid.Parse("20000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Create_invitation_revokes_the_old_one_and_persists_only_a_protected_token()
    {
        var invitations = new FakeInvitations();
        var old = MerchantUserInvitation.Create(
            MerchantId, "User@Example.com", InvitationTokens.Hash(InvitationTokens.New()),
            Now.AddHours(1), ActorId, Now);
        invitations.Add(old);
        var audits = new FakeAudits();
        var outbox = new FakeOutbox();
        var protector = new FakeProtector();
        var handler = new CreateInvitationHandler(
            invitations, audits, outbox, protector, new FakeUow(), new FakeClock());

        var result = await handler.Handle(
            new CreateInvitationCommand(" user@example.com ", MerchantId, ActorId, "corr-1", 24), default);

        Assert.Equal(Now, old.RevokedAt);
        var created = Assert.Single(invitations.Items, x => x.Id == result.InvitationId);
        Assert.Equal(InvitationTokens.Hash(protector.Raw!), created.TokenHash);
        Assert.DoesNotContain(protector.Raw!, created.TokenHash, StringComparison.Ordinal);
        var delivery = Assert.IsType<MerchantUserInvitationDeliveryRequested>(Assert.Single(outbox.Events));
        Assert.Equal(protector.Protected, delivery.ProtectedToken);
        Assert.DoesNotContain(protector.Raw!, delivery.ProtectedToken, StringComparison.Ordinal);
        Assert.Equal(
            [MerchantUserManagementAudit.Actions.InviteRevoke, MerchantUserManagementAudit.Actions.InviteCreate],
            audits.Items.Select(x => x.Action));
        Assert.NotEqual("user@example.com", result.MaskedEmail);
    }

    [Fact]
    public async Task Admin_invitation_persists_audience_roles_and_replays_same_idempotency_key()
    {
        var invitations = new FakeInvitations();
        var outbox = new FakeOutbox();
        var roles = new FakeRoles();
        roles.Seed("merchant_staff");
        var operations = new FakeAdminOperations();
        var handler = new CreateInvitationHandler(
            invitations, new FakeAudits(), outbox, new FakeProtector(), new FakeUow(), new FakeClock(),
            operations, roles);
        var command = new CreateInvitationCommand(
            "admin-invite@example.com", MerchantId, ActorId, "corr-admin", 24,
            InvitationActorAudience.Admin, ["merchant_staff"], "invite-key-1");

        var created = await handler.Handle(command, default);
        var replay = await handler.Handle(command, default);

        Assert.Equal(created, replay);
        var invitation = Assert.Single(invitations.Items);
        Assert.Equal(InvitationActorAudience.Admin, invitation.CreatedByAudience);
        Assert.Equal(["merchant_staff"], invitation.IntendedRoleCodes());
        Assert.Single(outbox.Events);
        Assert.Single(operations.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!")]
    public async Task Invitation_resolution_rejects_malformed_tokens_without_a_repository_read(string rawToken)
    {
        var invitations = new FakeInvitations();
        var result = await new ResolveInvitationTokenHandler(invitations, new FakeClock())
            .Handle(new ResolveInvitationTokenQuery(rawToken), default);

        Assert.Null(result);
        Assert.Equal(0, invitations.TokenReads);
    }

    [Fact]
    public async Task Approve_assigns_staff_once_and_repeated_approve_is_a_conflict()
    {
        var users = new FakeUsers();
        var user = PendingInvited();
        users.Seed(user);
        var roles = new FakeRoles();
        roles.Seed("merchant_staff");
        var audits = new FakeAudits();
        var sessions = new FakeSessions();
        var handler = new ChangeMerchantUserLifecycleHandler(
            users, roles, new FakeManagerGuard(2), sessions, audits, new FakeUow(), new FakeClock());
        var command = new ChangeMerchantUserLifecycleCommand(
            user.Id, MerchantId, ActorId, MerchantUserLifecycleAction.Approve, "corr-2");

        await handler.Handle(command, default);

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Single(roles.Assignments);
        Assert.Equal(MerchantUserManagementAudit.Actions.Approve, Assert.Single(audits.Items).Action);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(command, default).AsTask());
        Assert.Single(roles.Assignments);
    }

    [Fact]
    public async Task Suspending_the_last_manager_is_rejected_before_session_revocation()
    {
        var users = new FakeUsers();
        var user = PendingInvited();
        user.Approve(MerchantId, Now);
        users.Seed(user);
        var roles = new FakeRoles();
        var managerRoleId = roles.Seed("merchant_manager");
        roles.Assignments.Add(RoleAssignment.Create(user.Id, managerRoleId, MerchantId, ActorId, Now));
        var sessions = new FakeSessions();
        var handler = new ChangeMerchantUserLifecycleHandler(
            users, roles, new FakeManagerGuard(1), sessions, new FakeAudits(), new FakeUow(), new FakeClock());

        await Assert.ThrowsAsync<BuildingBlocks.Application.ConflictException>(() => handler.Handle(
            new ChangeMerchantUserLifecycleCommand(
                user.Id, MerchantId, ActorId, MerchantUserLifecycleAction.Suspend, "corr-3"), default).AsTask());

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Null(sessions.RevokedUserId);
    }

    [Fact]
    public async Task Detail_masks_pii_and_returns_roles_and_effective_permissions()
    {
        var users = new FakeUsers();
        var user = PendingInvited();
        user.SetDetails("สมชาย", "ใจดี", IdentityType.Individual,
            "1234567890123", "SALE-1", "LICENSE-9988", "0812345678");
        users.Seed(user);
        var roles = new FakeRoles
        {
            RoleCodes = ["merchant_staff"],
            EffectivePermissions = new HashSet<string> { "payment.view" },
        };

        var result = await new GetMerchantUserHandler(users, roles)
            .Handle(new GetMerchantUserQuery(user.Id, MerchantId), default);

        Assert.NotNull(result);
        Assert.NotEqual(user.Email, result!.MaskedEmail);
        Assert.EndsWith("5678", result.MaskedPhone);
        Assert.EndsWith("0123", result.MaskedIdentityNumber);
        Assert.EndsWith("9988", result.MaskedLicenseNumber);
        Assert.Equal(["merchant_staff"], result.RoleCodes);
        Assert.Contains("payment.view", result.EffectivePermissions);
    }

    private static User PendingInvited()
    {
        var user = User.RegisterInvited("google-sub", "person@example.com", MerchantId, Now);
        user.SetDetails("First", "Last", IdentityType.Individual, null, "SALE-1", null, null);
        return user;
    }

    private sealed class FakeClock : IClock { public DateTime UtcNow => Now; }

    private sealed class FakeUow : IUserUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            operation(ct);
    }

    private sealed class FakeInvitations : IInvitationRepository
    {
        public List<MerchantUserInvitation> Items { get; } = [];
        public int TokenReads { get; private set; }
        public void Add(MerchantUserInvitation invitation) => Items.Add(invitation);
        public Task<MerchantUserInvitation?> FindByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Items.FirstOrDefault(x => x.Id == id));
        public Task<MerchantUserInvitation?> FindPendingByNormalizedEmailAsync(string email, CancellationToken ct) =>
            Task.FromResult(Items.FirstOrDefault(x => x.NormalizedEmail == email && x.AcceptedAt is null && x.RevokedAt is null));
        public Task<MerchantUserInvitation?> FindByTokenHashUnfilteredAsync(string hash, CancellationToken ct)
        {
            TokenReads++;
            return Task.FromResult(Items.FirstOrDefault(x => x.TokenHash == hash));
        }
        public Task<MerchantUserInvitation?> FindByIdUnfilteredAsync(Guid id, CancellationToken ct) =>
            FindByIdAsync(id, ct);
    }

    private sealed class FakeAudits : IManagementAuditWriter
    {
        public List<MerchantUserManagementAudit> Items { get; } = [];
        public void Append(MerchantUserManagementAudit audit) => Items.Add(audit);
    }

    private sealed class FakeOutbox : IRegistrationOutboxWriter
    {
        public List<INotification> Events { get; } = [];
        public void Enqueue(INotification notification) => Events.Add(notification);
    }

    private sealed class FakeAdminOperations : IAdminUserOperationStore
    {
        public List<AdminUserOperationRecord> Items { get; } = [];
        public Task<AdminUserOperationRecord?> FindAsync(
            Guid? merchantId, Guid actorId, string operation, string idempotencyKey, CancellationToken ct) =>
            Task.FromResult(Items.SingleOrDefault(x => x.MerchantId == merchantId && x.ActorId == actorId
                && x.Operation == operation && x.IdempotencyKey == idempotencyKey));
        public void Add(AdminUserOperationRecord record) => Items.Add(record);
    }

    private sealed class FakeProtector : IInvitationDeliveryProtector
    {
        public string? Raw { get; private set; }
        public string Protected { get; private set; } = "";
        public string Protect(string rawToken)
        {
            Raw = rawToken;
            Protected = $"protected:{rawToken.Length}:{rawToken[..2]}";
            return Protected;
        }
        public bool TryUnprotect(string protectedToken, out string rawToken)
        {
            rawToken = Raw ?? "";
            return protectedToken == Protected;
        }
    }

    private sealed class FakeUsers : IUserRepository
    {
        private readonly Dictionary<Guid, User> _items = [];
        public void Seed(User user) => _items[user.Id] = user;
        public Task<User?> FindBySubjectAsync(string subject, CancellationToken ct) =>
            Task.FromResult(_items.Values.FirstOrDefault(x => x.Subject == subject));
        public Task<User?> FindByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_items.GetValueOrDefault(id));
        public void Add(User account) => Seed(account);
    }

    private sealed class FakeRoles : IRoleRepository
    {
        private readonly Dictionary<string, Guid> _ids = [];
        public List<RoleAssignment> Assignments { get; } = [];
        public IReadOnlyList<string> RoleCodes { get; init; } = [];
        public IReadOnlySet<string> EffectivePermissions { get; init; } = new HashSet<string>();
        public Guid Seed(string code) => _ids[code] = Guid.NewGuid();
        public void AddAssignment(RoleAssignment assignment) => Assignments.Add(assignment);
        public void RemoveAssignment(RoleAssignment assignment) => Assignments.Remove(assignment);
        public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
            Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken ct) => Resolve(codes);
        public Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(
            Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken ct) => Resolve(codes);
        private Task<IReadOnlyDictionary<string, Guid>> Resolve(IReadOnlyCollection<string> codes) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(
                _ids.Where(x => codes.Contains(x.Key)).ToDictionary());
        public Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(Assignments.Where(x => x.UserId == userId).Select(x => x.RoleId).ToHashSet());
        public Task<RoleAssignment?> GetAssignmentAsync(Guid userId, Guid roleId, CancellationToken ct) =>
            Task.FromResult(Assignments.FirstOrDefault(x => x.UserId == userId && x.RoleId == roleId));
        public Task<bool> AssignmentExistsAsync(Guid userId, Guid roleId, CancellationToken ct) =>
            Task.FromResult(Assignments.Any(x => x.UserId == userId && x.RoleId == roleId));
        public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid userId, Guid merchantId, CancellationToken ct) =>
            Task.FromResult(EffectivePermissions);
        public Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid userId, Guid merchantId, CancellationToken ct) =>
            Task.FromResult(RoleCodes);
    }

    private sealed class FakeManagerGuard(int count) : IActiveManagerGuard
    {
        public Task<int> CountActiveUsersWithRoleAsync(Guid merchantId, Guid roleId, CancellationToken ct) =>
            Task.FromResult(count);
    }

    private sealed class FakeSessions : ISessionStore
    {
        public Guid? RevokedUserId { get; private set; }
        public Task<Session?> FindByTokenHashAsync(byte[] hash, CancellationToken ct) => Task.FromResult<Session?>(null);
        public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => Task.FromResult<Guid?>(null);
        public void Add(Session session) => throw new NotSupportedException();
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
        public Task<bool> TrySupersedeAsync(Guid sessionId, Guid successorId, DateTime now, CancellationToken ct) =>
            Task.FromResult(false);
        public Task SlideIdleAsync(Guid sessionId, DateTime idleExpiresAt, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
        {
            RevokedUserId = userId;
            return Task.CompletedTask;
        }
        public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
    }
}
