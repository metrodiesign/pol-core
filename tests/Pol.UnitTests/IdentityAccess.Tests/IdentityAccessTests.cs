using Access.Domain;
using Accounts.Application;
using Accounts.Domain;

namespace IdentityAccess.Tests;

[Trait("Capability", "IdentityAccess")]
public sealed class IdentityAccessTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-2.1")]
    public void External_identity_is_the_stable_key_and_email_is_observation_only()
    {
        var identity = ExternalIdentity.Create("microsoft", "tenant-1", "oid-1");
        var account = Account.Create(AccountType.Employee, "Employee", Now);
        var login = LoginAccount.Create(account.Id, identity, "first@example.test", "First", Now);

        login.Observe("changed@example.test", "Changed", Now.AddMinutes(1));

        Assert.Equal(identity, login.Identity);
        Assert.Equal("changed@example.test", login.Email);
        Assert.Equal("Changed", login.DisplayName);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.3")]
    public void Human_identity_policy_rejects_every_protocol_boundary_failure()
    {
        var valid = Verified("issuer", "tenant-1", "audience");
        var invalid = new[]
        {
            valid with { Identity = ExternalIdentity.Create("other", "tenant-1", "oid-1") },
            valid with { Identity = ExternalIdentity.Create("microsoft", "other", "oid-1") },
            valid with { Issuer = "other" },
            valid with { Audience = "other" },
            valid with { SignatureValidated = false },
            valid with { LifetimeValidated = false },
            valid with { StateValidated = false },
            valid with { NonceValidated = false },
        };

        foreach (var candidate in invalid)
            Assert.False(HumanIdentityPolicy.Validate(candidate, "issuer", "tenant-1", "audience").IsValid);
        Assert.True(HumanIdentityPolicy.Validate(valid, "issuer", "tenant-1", "audience").IsValid);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.4")]
    [Trait("Requirement", "REQ-2.5")]
    public async Task Employee_jit_requires_workforce_role_and_never_grants_access()
    {
        var store = new RecordingEmployeeStore();
        var service = new EmployeeJitService(store);
        var identity = Verified("issuer", "tenant-1", "audience") with { WorkforceEligible = true };

        var result = await service.ResolveAsync(identity, "issuer", "tenant-1", "audience", default);

        Assert.True(result.Created);
        Assert.False(result.HasPlatformAccess);
        Assert.False(result.HasMerchantAccess);
        Assert.Equal(1, store.Calls);

        var ineligible = await Assert.ThrowsAsync<IdentityAccessException>(() =>
            service.ResolveAsync(identity with { WorkforceEligible = false }, "issuer", "tenant-1", "audience", default));
        Assert.Equal("workforce_not_eligible", ineligible.Code);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.12")]
    public async Task Registration_session_is_limited_to_verified_identity_and_merchant()
    {
        var store = new RecordingRegistrationStore(approved: false);
        var service = new RegistrationSessionService(store);
        var merchantId = Guid.NewGuid();
        var result = await service.StartAsync(
            Verified("issuer", "tenant-1", "audience"), merchantId, Now, TimeSpan.FromMinutes(10),
            "issuer", "tenant-1", "audience", default);

        Assert.Equal(merchantId, result.Session.MerchantId);
        Assert.True(result.Session.IsLiveAt(Now.AddMinutes(1)));
        Assert.NotEmpty(result.RawReference);
        Assert.Equal(1, store.Calls);

        var approved = new RegistrationSessionService(new RecordingRegistrationStore(approved: true));
        var exception = await Assert.ThrowsAsync<IdentityAccessException>(() => approved.StartAsync(
            Verified("issuer", "tenant-1", "audience"), merchantId, Now, TimeSpan.FromMinutes(10),
            "issuer", "tenant-1", "audience", default));
        Assert.Equal("account_already_approved", exception.Code);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.7")]
    [Trait("Requirement", "REQ-2.8")]
    [Trait("Requirement", "REQ-2.9")]
    public async Task System_assertion_accepts_one_jti_and_rejects_replay_or_key_policy_failure()
    {
        var replay = new RecordingReplayStore();
        var service = new SystemClientAssertionService(replay);
        var account = Account.Create(AccountType.System, "System", Now);
        var client = SystemClient.Create(account.Id, "client-1", Guid.NewGuid(), "SANDBOX", ["client_credentials"], Now);
        var key = ClientKeyPolicy.Create(client.Id, "app-1", "kid-1", "PS256", Now.AddMinutes(-1), Now.AddMinutes(5));
        var assertion = new ClientAssertion("app-1", "app-1", "kid-1", "PS256", "jti-1",
            Now, Now.AddSeconds(30), "/oauth/token");

        Assert.True((await service.ValidateAsync(client, key, assertion, Now, "/oauth/token",
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), default)).IsValid);
        var replayed = await service.ValidateAsync(client, key, assertion, Now, "/oauth/token",
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), default);
        Assert.Equal("assertion_replayed", replayed.Code);

        key.Revoke();
        var revoked = await service.ValidateAsync(client, key, assertion with { Jti = "jti-2" }, Now,
            "/oauth/token", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), default);
        Assert.Equal("key_inactive", revoked.Code);

        var expired = ClientKeyPolicy.Create(client.Id, "app-1", "kid-2", "PS256", Now.AddMinutes(-5), Now.AddMinutes(-1));
        var expiredResult = await service.ValidateAsync(client, expired, assertion with { KeyId = "kid-2", Jti = "jti-3" }, Now,
            "/oauth/token", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), default);
        Assert.Equal("key_inactive", expiredResult.Code);

        client.Suspend(Now.AddMinutes(1));
        var disabled = await service.ValidateAsync(client, key, assertion with { Jti = "jti-4" }, Now,
            "/oauth/token", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), default);
        Assert.Equal("client_disabled", disabled.Code);

        var enabledClient = SystemClient.Create(account.Id, "client-2", Guid.NewGuid(), "SANDBOX", ["client_credentials"], Now);
        account.Suspend(Now.AddMinutes(2));
        var accountDisabled = await service.ValidateAsync(account, enabledClient, key,
            assertion with { ApplicationId = "app-1", Jti = "jti-5" }, Now,
            "/oauth/token", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), default);
        Assert.Equal("account_disabled", accountDisabled.Code);

        Assert.Throws<ArgumentException>(() =>
            ClientKeyPolicy.Create(client.Id, "app-1", "kid-hs", "HS256", Now, Now.AddMinutes(5)));
    }

    [Fact]
    [Trait("Requirement", "REQ-2.10")]
    [Trait("Requirement", "REQ-2.14")]
    public void Account_and_bff_ticket_kill_switches_invalidate_old_context()
    {
        var account = Account.Create(AccountType.Employee, "Employee", Now);
        var ticket = BffSessionTicket.Create(
            new byte[32], account.Id, null, "protected", account.AuthorizationVersion,
            Now, Now.AddHours(1));

        Assert.True(ticket.IsLiveAt(Now.AddMinutes(1), account.AuthorizationVersion));
        account.Suspend(Now.AddMinutes(2));
        Assert.False(ticket.IsLiveAt(Now.AddMinutes(3), account.AuthorizationVersion));
        ticket.Revoke(Now.AddMinutes(3));
        Assert.False(ticket.IsLiveAt(Now.AddMinutes(4), account.AuthorizationVersion));
    }

    [Fact]
    [Trait("Requirement", "REQ-3.1")]
    [Trait("Requirement", "REQ-3.2")]
    [Trait("Requirement", "REQ-3.3")]
    [Trait("Requirement", "REQ-3.4")]
    [Trait("Requirement", "REQ-3.6")]
    [Trait("Requirement", "REQ-3.7")]
    [Trait("Requirement", "REQ-3.8")]
    public void Access_evaluator_is_deny_by_default_and_enforces_scope()
    {
        var merchantId = Guid.NewGuid();
        var otherMerchantId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var snapshot = Snapshot(AccountType.Agent, merchantId, DataScope.Self, saleId, branchId);

        Assert.Equal(AuthorizationDecisionReason.MissingMerchantContext,
            AccessEvaluator.CanReadOrder(snapshot with { MerchantId = null }, merchantId, saleId, branchId).Reason);
        Assert.Equal(AuthorizationDecisionReason.MerchantMismatch,
            AccessEvaluator.CanReadOrder(snapshot, otherMerchantId, saleId, branchId).Reason);
        Assert.True(AccessEvaluator.CanReadOrder(snapshot, merchantId, saleId, null).Allowed);
        Assert.False(AccessEvaluator.CanReadOrder(snapshot with { AccountType = AccountType.Employee }, merchantId,
            saleId, branchId).Allowed);

        var branch = Snapshot(AccountType.Employee, merchantId, DataScope.Branch, null, branchId);
        Assert.True(AccessEvaluator.CanReadOrder(branch, merchantId, null, branchId).Allowed);
        Assert.False(AccessEvaluator.CanReadOrder(branch, merchantId, null, Guid.NewGuid()).Allowed);
        Assert.False(AccessEvaluator.CanReadOrder(
            branch with { BranchIds = new HashSet<Guid>() }, merchantId, null, branchId).Allowed);
        Assert.False(AccessEvaluator.CanReadOrder(
            branch with { BranchIds = new HashSet<Guid>([branchId, Guid.NewGuid()]) }, merchantId, null, branchId).Allowed);
        Assert.True(AccessEvaluator.CanReadOrder(
            branch with { HomeBranchId = null }, merchantId, null, branchId).Allowed);
        var assignedBranches = Snapshot(AccountType.Agent, merchantId, DataScope.AssignedBranches, saleId, branchId);
        Assert.True(AccessEvaluator.CanReadOrder(assignedBranches, merchantId, null, branchId).Allowed);
        Assert.False(AccessEvaluator.CanReadOrder(assignedBranches, merchantId, null, Guid.NewGuid()).Allowed);
        Assert.False(AccessEvaluator.CanReadOrder(
            Snapshot(AccountType.System, merchantId, DataScope.Self, null, branchId), merchantId, null, branchId).Allowed);
        Assert.False(AccessEvaluator.CanUsePlatform(
            Snapshot(AccountType.Agent, merchantId, DataScope.Merchant, saleId, branchId)));
    }

    [Fact]
    [Trait("Requirement", "REQ-3.10")]
    [Trait("Requirement", "REQ-3.11")]
    [Trait("Requirement", "REQ-3.12")]
    public void Access_grant_ceiling_and_stale_version_are_fail_closed()
    {
        var merchantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var grantor = Snapshot(AccountType.Employee, merchantId, DataScope.Branch, null, branchId) with
        {
            HasPlatformAccess = false,
            Permissions = new HashSet<string>(["access.manage"]),
            RoleIds = new HashSet<Guid>([roleId]),
        };

        Assert.Equal(AuthorizationDecisionReason.GrantExceedsCaller,
            AccessEvaluator.CanAssign(grantor, AccountType.Agent, merchantId, DataScope.Branch,
                [Guid.NewGuid()], [roleId]).Reason);
        Assert.Equal(AuthorizationDecisionReason.StaleAuthorization,
            AccessEvaluator.VerifyTokenVersion(1, 2).Reason);
        Assert.True(AccessEvaluator.VerifyTokenVersion(2, 2).Allowed);
        Assert.Equal(AuthorizationDecisionReason.SelfNotSupported,
            AccessEvaluator.CanAssign(grantor, AccountType.Employee, merchantId, DataScope.Self, [], []).Reason);

        Assert.Equal(AuthorizationDecisionReason.MerchantMismatch,
            AccessEvaluator.ValidateReferenceMerchants(
                merchantId, [merchantId], [Guid.NewGuid()], saleMerchantId: merchantId).Reason);
        Assert.True(AccessEvaluator.ValidateReferenceMerchants(
            merchantId, [merchantId], [merchantId], saleMerchantId: merchantId).Allowed);
    }

    private static VerifiedHumanIdentity Verified(string issuer, string tenant, string audience) =>
        new(
            ExternalIdentity.Create("microsoft", tenant, "oid-1"),
            issuer,
            audience,
            SignatureValidated: true,
            LifetimeValidated: true,
            StateValidated: true,
            NonceValidated: true,
            WorkforceEligible: false,
            Email: "user@example.test",
            DisplayName: "User");

    private static AuthorizationSnapshot Snapshot(
        AccountType accountType,
        Guid merchantId,
        DataScope scope,
        Guid? saleId,
        Guid branchId) =>
        new(
            Guid.NewGuid(),
            accountType,
            AccountStatus.Active,
            1,
            merchantId,
            scope,
            saleId,
            branchId,
            new HashSet<Guid>([branchId]),
            new HashSet<Guid>(),
            HasPlatformAccess: accountType == AccountType.Employee,
            new HashSet<string>(),
            null);

    private sealed class RecordingEmployeeStore : IEmployeeJitStore
    {
        public int Calls { get; private set; }

        public Task<EmployeeJitResult> GetOrCreateAsync(VerifiedHumanIdentity identity, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new EmployeeJitResult(Guid.NewGuid(), true, false, false, 0));
        }
    }

    private sealed class RecordingRegistrationStore(bool approved) : IRegistrationSessionStore
    {
        public int Calls { get; private set; }

        public Task<bool> HasApprovedAccountAsync(ExternalIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult(approved);

        public Task<RegistrationSession> IssueAsync(
            ExternalIdentity identity,
            Guid merchantId,
            byte[] sessionReferenceHash,
            DateTime now,
            TimeSpan lifetime,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(RegistrationSession.Issue(sessionReferenceHash, identity, merchantId, now, lifetime));
        }
    }

    private sealed class RecordingReplayStore : IAssertionReplayStore
    {
        private readonly HashSet<string> _claims = new(StringComparer.Ordinal);

        public Task<bool> TryClaimAsync(string applicationId, string jti, DateTime expiresAt, CancellationToken cancellationToken) =>
            Task.FromResult(_claims.Add($"{applicationId}:{jti}"));
    }
}
