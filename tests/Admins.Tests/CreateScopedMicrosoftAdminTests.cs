using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;

namespace Admins.Tests;

public sealed class CreateScopedMicrosoftAdminTests
{
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ObjectId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid ActorId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    public static TheoryData<string?> InvalidApprovalReferences() => new()
    {
        null,
        "",
        "   ",
        new string('x', CreateScopedHandler.ApprovalReferenceMaxLength + 1),
    };

    [Fact]
    public async Task Create_derives_the_singleton_tenant_and_persists_a_canonical_active_scoped_tuple()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var tenant = new FakeWorkforceTenantBindingStore(TenantId);
        var unitOfWork = new FakeUnitOfWork();

        var result = await Handler(admins, audit, tenant, unitOfWork).Handle(
            Command(email: "  Contact-Label  ", approvalReference: "  entra-export-42  "), default);

        var account = Assert.Single(admins.Accounts);
        Assert.Equal(result.AdminId, account.Id);
        Assert.Equal(User.MicrosoftProvider, account.Provider);
        Assert.Equal(TenantId, account.TenantId);
        Assert.Equal(ObjectId.ToString("D"), account.Subject);
        Assert.Equal("Contact-Label", account.Email);
        Assert.Equal(Tier.Scoped, account.Tier);
        Assert.Equal(UserStatus.Active, account.Status);
        Assert.Equal(1, account.Version);
        Assert.Equal(0, account.AuthorizationVersion);
        Assert.Empty(admins.Assignments);
        Assert.Equal(1, tenant.GetRequiredCalls);
        Assert.Equal(1, admins.MicrosoftIdentityLookupCalls);
        Assert.Equal(0, admins.GenericIdentityLookupCalls);
        Assert.Equal(0, admins.EmailLookupCalls);
        Assert.Equal(1, unitOfWork.ExecuteInTransactionCalls);
        Assert.Equal(1, unitOfWork.SaveChangesCalls);

        var entry = Assert.Single(audit.Appended);
        Assert.Equal(AuditAction.CreateScoped, entry.Action);
        Assert.Equal(ActorId, entry.ActorId);
        Assert.Equal(account.Id, entry.TargetAdminId);
        Assert.Equal("entra-export-42", entry.CorrelationId);
        Assert.NotEqual("http-correlation", entry.CorrelationId);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  duplicate@example.com  ", "duplicate@example.com")]
    public async Task Optional_contact_is_normalized_without_becoming_an_identity_or_uniqueness_gate(
        string? suppliedEmail, string? expectedEmail)
    {
        var admins = new FakePlatformUserRepository();
        admins.Add(User.CreateScopedMicrosoft(TenantId, Guid.NewGuid(), "duplicate@example.com", Now));
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(
            admins, audit, new FakeWorkforceTenantBindingStore(TenantId), new FakeUnitOfWork())
            .Handle(Command(email: suppliedEmail), default);

        var created = Assert.Single(admins.Accounts, account => account.Id == result.AdminId);
        Assert.Equal(expectedEmail, created.Email);
        Assert.Equal(ObjectId.ToString("D"), created.Subject);
        Assert.Equal(0, admins.EmailLookupCalls);
        Assert.Equal(2, admins.Accounts.Count);
    }

    [Fact]
    public async Task Overlength_contact_is_stored_as_null_without_blocking_the_verified_tuple()
    {
        var admins = new FakePlatformUserRepository();

        var result = await Handler(
            admins, new FakePlatformUserAuditWriter(),
            new FakeWorkforceTenantBindingStore(TenantId), new FakeUnitOfWork())
            .Handle(Command(email: new string('x', AdminContactEmail.MaxLength + 1)), default);

        Assert.Null(Assert.Single(admins.Accounts, account => account.Id == result.AdminId).Email);
    }

    [Theory]
    [MemberData(nameof(InvalidApprovalReferences))]
    public async Task Missing_blank_or_overlength_approval_reference_is_rejected_before_any_write(
        string? approvalReference)
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var tenant = new FakeWorkforceTenantBindingStore(TenantId);
        var unitOfWork = new FakeUnitOfWork();

        await Assert.ThrowsAsync<ArgumentException>(async () => await Handler(
            admins, audit, tenant, unitOfWork).Handle(
                Command(approvalReference: approvalReference!), default));

        Assert.Empty(admins.Accounts);
        Assert.Empty(audit.Appended);
        Assert.Equal(0, admins.IdentityMutationLockCalls);
        Assert.Equal(0, tenant.GetRequiredCalls);
        Assert.Equal(0, unitOfWork.ExecuteInTransactionCalls);
        Assert.Equal(0, unitOfWork.SaveChangesCalls);
    }

    [Fact]
    public async Task Empty_object_id_is_rejected_before_any_write()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var tenant = new FakeWorkforceTenantBindingStore(TenantId);
        var unitOfWork = new FakeUnitOfWork();

        await Assert.ThrowsAsync<ArgumentException>(async () => await Handler(
            admins, audit, tenant, unitOfWork).Handle(Command(objectId: Guid.Empty), default));

        Assert.Empty(admins.Accounts);
        Assert.Empty(audit.Appended);
        Assert.Equal(0, admins.IdentityMutationLockCalls);
        Assert.Equal(0, tenant.GetRequiredCalls);
        Assert.Equal(0, unitOfWork.ExecuteInTransactionCalls);
        Assert.Equal(0, unitOfWork.SaveChangesCalls);
    }

    [Fact]
    public async Task Exact_tuple_conflict_is_rejected_without_a_second_account_or_audit()
    {
        var admins = new FakePlatformUserRepository();
        var existing = User.CreateScopedMicrosoft(TenantId, ObjectId, "existing@example.com", Now);
        admins.Add(existing);
        var audit = new FakePlatformUserAuditWriter();
        var unitOfWork = new FakeUnitOfWork();

        await Assert.ThrowsAsync<ConflictException>(async () => await Handler(
            admins, audit, new FakeWorkforceTenantBindingStore(TenantId), unitOfWork)
            .Handle(Command(email: "other@example.com"), default));

        Assert.Same(existing, Assert.Single(admins.Accounts));
        Assert.Empty(audit.Appended);
        Assert.Equal(1, admins.MicrosoftIdentityLookupCalls);
        Assert.Equal(0, admins.EmailLookupCalls);
        Assert.Equal(1, unitOfWork.ExecuteInTransactionCalls);
        Assert.Equal(0, unitOfWork.SaveChangesCalls);
    }

    [Fact]
    public async Task Missing_tenant_binding_fails_before_account_or_audit_write()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var tenant = new FakeWorkforceTenantBindingStore(TenantId)
        {
            Failure = new InvalidOperationException("Tenant binding is unavailable."),
        };
        var unitOfWork = new FakeUnitOfWork();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Handler(
            admins, audit, tenant, unitOfWork).Handle(Command(), default));

        Assert.Empty(admins.Accounts);
        Assert.Empty(audit.Appended);
        Assert.Equal(1, admins.IdentityMutationLockCalls);
        Assert.Equal(1, tenant.GetRequiredCalls);
        Assert.Equal(0, admins.MicrosoftIdentityLookupCalls);
        Assert.Equal(1, unitOfWork.ExecuteInTransactionCalls);
        Assert.Equal(0, unitOfWork.SaveChangesCalls);
    }

    [Fact]
    public async Task Nullable_contact_flows_through_admin_list_and_detail_application_contracts()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.CreateScopedMicrosoft(TenantId, ObjectId, email: null, Now);
        admins.Add(account);

        var page = await new ListAdminsHandler(admins).Handle(new ListAdminsQuery(), default);
        var detail = await new GetAdminByIdHandler(
            admins, new FakeAdminRoleRepository(), new FakeProfileLookup())
            .Handle(new GetAdminByIdQuery(account.Id), default);

        Assert.Null(Assert.Single(page.Items).Email);
        Assert.NotNull(detail);
        Assert.Null(detail!.Email);
    }

    [Fact]
    public async Task First_exact_login_resolves_the_prebound_admin_id_without_jit_or_contact_matching()
    {
        var admins = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var audit = new FakePlatformUserAuditWriter();
        var tenant = new FakeWorkforceTenantBindingStore(TenantId);
        var unitOfWork = new FakeUnitOfWork();
        var created = await Handler(admins, audit, tenant, unitOfWork)
            .Handle(Command(email: "invite@example.com"), default);
        var resolver = new ResolveMicrosoftAdminHandler(
            admins,
            roles,
            audit,
            new NoRecoveryReader(),
            new UnexpectedEmployeeProfileReader(),
            unitOfWork,
            new FixedClock { UtcNow = Now.AddMinutes(1) });

        var result = await resolver.Handle(new ResolveMicrosoftAdminCommand(
            TenantId, ObjectId, "renamed@example.com", EmployeeId: null, "login-correlation"), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(created.AdminId, result.Resolution!.AdminId);
        Assert.Equal("invite@example.com", result.Resolution.Email);
        Assert.Single(admins.Accounts);
        Assert.Empty(roles.Assignments);
        Assert.Equal(AuditAction.CreateScoped, Assert.Single(audit.Appended).Action);
        Assert.Equal(0, admins.EmailLookupCalls);
    }

    private static CreateScopedHandler Handler(
        FakePlatformUserRepository admins,
        FakePlatformUserAuditWriter audit,
        FakeWorkforceTenantBindingStore tenant,
        FakeUnitOfWork unitOfWork) =>
        new(admins, audit, new FakeProfileLookup(), tenant, unitOfWork, new FixedClock { UtcNow = Now });

    private static CreateScopedCommand Command(
        Guid? objectId = null,
        string? email = "contact@example.com",
        string approvalReference = "entra-export-42") =>
        new(objectId ?? ObjectId, email, approvalReference, ActorId, "http-correlation");

    private sealed class NoRecoveryReader : IAdminIdentityRecoveryReader
    {
        public Task<ResolveResult> ResolveAfterConflictAsync(
            Guid tenantId, Guid objectId, CancellationToken cancellationToken) =>
            Task.FromResult(ResolveResult.IdentityConflict);
    }

    private sealed class UnexpectedEmployeeProfileReader : IEmployeeProfileReader
    {
        public Task<EmployeeProfileLookup> LookupAsync(
            string normalizedEmployeeId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Employee profile must not be read when the switch is off.");
    }
}
