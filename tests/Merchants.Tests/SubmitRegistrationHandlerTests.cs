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

/// <summary>
/// The <see cref="SubmitRegistrationHandler"/> orchestration (REQ-3/4/5/7/20/21): a valid Registration submission
/// creates a Pending account (with its person details) + external login and enqueues an audit + a registration event;
/// a Correction resubmits the EXISTING rejected account (no second login) and re-enqueues the event; identity is taken
/// from the stateless ticket, never the form; DisplayName is computed from first/last name. Person details live on the
/// account itself (a "merchant" is the company/app, not the person). Persistence is faked — DB-level atomicity and the
/// unique-violation→409 replay/duplicate guard are proven in the integration suite.
/// </summary>
public sealed class SubmitRegistrationHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_valid_registration_ticket_creates_pending_account_with_details_login_audit_and_event()
    {
        var ctx = new Ctx();
        var cmd = RegistrationCommand(firstName: "Acme", lastName: "Co");

        var result = await ctx.Handler.Handle(cmd, default);

        var account = Assert.Single(ctx.Users.Added);
        Assert.Equal("g-sub-1", account.Subject);          // from the ticket
        Assert.Equal("p@org.com", account.Email);
        Assert.Equal(UserStatus.PendingApproval, account.Status); // no merchant until approval
        Assert.Equal(UserStatus.PendingApproval, result.Status);
        Assert.Equal(account.Id, result.UserId);

        Assert.Equal("Acme", account.FirstName);           // person details land on the account, not a profile
        Assert.Equal("Co", account.LastName);
        Assert.Equal("Acme Co", account.DisplayName);      // computed from first + last name

        var login = Assert.Single(ctx.Logins.Added);
        Assert.Equal("g-sub-1", login.Subject);
        Assert.Equal(ExternalLogin.Google, login.Provider);

        var audit = Assert.Single(ctx.Audits.Appended);
        Assert.Equal(RegistrationAuditAction.Registered, audit.Action);
        Assert.Equal("g-sub-1", audit.TargetSubject);
        Assert.Null(audit.ActorSubject);                   // self-service, no admin actor

        var evt = Assert.IsType<MerchantUserRegistrationSubmitted>(Assert.Single(ctx.Outbox.Enqueued));
        Assert.Equal(account.Id, evt.UserId);
        Assert.Equal(Now, evt.OccurredAt);
        Assert.True(ctx.Uow.SaveCalls >= 1);
    }

    [Fact]
    public async Task A_microsoft_ticket_stores_the_microsoft_provider_on_the_external_login()
    {
        var ctx = new Ctx();
        var cmd = RegistrationCommand() with { Provider = ExternalLogin.Microsoft };

        await ctx.Handler.Handle(cmd, default);

        var login = Assert.Single(ctx.Logins.Added);
        Assert.Equal(ExternalLogin.Microsoft, login.Provider);
    }

    [Fact]
    public async Task A_photo_is_stored_and_its_key_is_set_on_the_account()
    {
        var ctx = new Ctx();
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xE0];
        var cmd = RegistrationCommand() with { PhotoBytes = bytes, PhotoContentType = PhotoValidation.Jpeg };

        await ctx.Handler.Handle(cmd, default);

        Assert.Equal(bytes, ctx.Photos.PutBytes);
        Assert.Equal(PhotoValidation.Jpeg, ctx.Photos.PutContentType);
        var account = Assert.Single(ctx.Users.Added);
        Assert.Equal(ctx.Photos.ReturnedKey, account.PhotoObjectKey);
        Assert.Equal(PhotoValidation.Jpeg, account.PhotoContentType);
    }

    [Fact]
    public async Task Optional_KYC_photo_is_staged_and_persisted_by_key_only_with_lifecycle_event()
    {
        var ctx = new Ctx();
        var operationId = Guid.NewGuid();
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var cmd = RegistrationCommand() with
        {
            KycPhotoBytes = bytes,
            KycPhotoContentType = PhotoValidation.Png,
            KycOperationId = operationId,
        };

        var result = await ctx.Handler.Handle(cmd, default);

        Assert.Equal(operationId, ctx.Photos.StagedOperationId);
        Assert.Equal(bytes, ctx.Photos.StagedBytes);
        Assert.Equal(PhotoValidation.Png, ctx.Photos.StagedContentType);
        var account = Assert.Single(ctx.Users.Added);
        Assert.Equal(ctx.Photos.ReturnedStagedKey, account.KycPhotoObjectKey);
        Assert.Equal(account.Id, result.UserId);

        var lifecycle = Assert.Single(ctx.Outbox.Enqueued.OfType<KycPhotoLifecycleRequested>());
        Assert.Equal(ctx.Photos.ReturnedStagedKey, lifecycle.NewObjectKey);
        Assert.Null(lifecycle.OldObjectKey);
        Assert.Single(ctx.Outbox.Enqueued.OfType<MerchantUserRegistrationSubmitted>());
    }

    [Fact]
    public async Task Correction_without_KYC_photo_preserves_existing_key_and_emits_no_lifecycle_event()
    {
        var ctx = new Ctx();
        var existing = RejectedUserWithKyc("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg");
        ctx.Users.Seed(existing);

        await ctx.Handler.Handle(RegistrationCommand() with { Purpose = TicketPurpose.Correction }, default);

        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg", existing.KycPhotoObjectKey);
        Assert.Null(ctx.Photos.StagedOperationId);
        Assert.Empty(ctx.Outbox.Enqueued.OfType<KycPhotoLifecycleRequested>());
    }

    [Fact]
    public async Task Correction_with_new_KYC_photo_replaces_key_and_requests_old_object_deletion()
    {
        var ctx = new Ctx();
        const string oldKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg";
        var existing = RejectedUserWithKyc(oldKey);
        ctx.Users.Seed(existing);

        await ctx.Handler.Handle(RegistrationCommand() with
        {
            Purpose = TicketPurpose.Correction,
            KycPhotoBytes = [0xFF, 0xD8, 0xFF],
            KycPhotoContentType = PhotoValidation.Jpeg,
            KycOperationId = Guid.NewGuid(),
        }, default);

        Assert.Equal(ctx.Photos.ReturnedStagedKey, existing.KycPhotoObjectKey);
        var lifecycle = Assert.Single(ctx.Outbox.Enqueued.OfType<KycPhotoLifecycleRequested>());
        Assert.Equal(oldKey, lifecycle.OldObjectKey);
    }

    [Fact]
    public async Task Database_failure_best_effort_deletes_new_staged_KYC_object()
    {
        var ctx = new Ctx();
        ctx.Uow.SaveException = new InvalidOperationException("database failed");
        var command = RegistrationCommand() with
        {
            KycPhotoBytes = [0xFF, 0xD8, 0xFF],
            KycPhotoContentType = PhotoValidation.Jpeg,
            KycOperationId = Guid.NewGuid(),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Handler.Handle(command, default).AsTask());

        Assert.Contains(ctx.Photos.ReturnedStagedKey, ctx.Photos.DiscardedStagedKeys);
        Assert.Empty(ctx.Photos.DeletedKeys);
    }

    [Fact]
    public async Task Database_failure_never_discards_a_staged_key_this_attempt_did_not_create()
    {
        // Codex review #191 (PR pull#191): a retried request resolves the SAME deterministic staged key (same
        // KycOperationId). If the retry's own insert then fails (e.g. the earlier attempt already committed and
        // this is a duplicate), discarding that key would delete the object the earlier, WINNING attempt's outbox
        // event still needs to commit later. PutStagedAsync reporting CreatedNew=false must suppress the discard.
        var ctx = new Ctx();
        ctx.Photos.ReturnedStagedCreatedNew = false;
        ctx.Uow.SaveException = new InvalidOperationException("database failed");
        var command = RegistrationCommand() with
        {
            KycPhotoBytes = [0xFF, 0xD8, 0xFF],
            KycPhotoContentType = PhotoValidation.Jpeg,
            KycOperationId = Guid.NewGuid(),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Handler.Handle(command, default).AsTask());

        Assert.DoesNotContain(ctx.Photos.ReturnedStagedKey, ctx.Photos.DiscardedStagedKeys);
        Assert.Empty(ctx.Photos.DeletedKeys);
    }

    private static User RejectedUserWithKyc(string key)
    {
        var user = User.Register("google", "g-sub-1", "p@org.com", Now);
        user.SetDetails("Old", "Name", IdentityType.Individual, null, null, null, null);
        user.SetKycPhoto(key);
        user.Reject(Now);
        return user;
    }

    [Fact]
    public async Task A_correction_ticket_resubmits_the_existing_rejected_account_without_a_second_login()
    {
        var ctx = new Ctx();
        var existing = User.Register("google", "g-sub-1", "p@org.com", Now);
        existing.SetDetails("Old", "Name", IdentityType.Individual, null, null, null, null);
        existing.Reject(Now);                            // PendingApproval -> Rejected
        ctx.Users.Seed(existing);

        var cmd = RegistrationCommand(firstName: "New", lastName: "Name") with { Purpose = TicketPurpose.Correction };
        var result = await ctx.Handler.Handle(cmd, default);

        Assert.Empty(ctx.Users.Added);                   // edits the existing record, never a second account
        Assert.Empty(ctx.Logins.Added);                  // no second external login (REQ-5.4)
        Assert.Equal(UserStatus.PendingApproval, existing.Status); // Rejected -> Pending
        Assert.Equal(existing.Id, result.UserId);
        Assert.Equal("New Name", existing.DisplayName);  // account updated in place (computed from first + last)

        var audit = Assert.Single(ctx.Audits.Appended);
        Assert.Equal(RegistrationAuditAction.Resubmitted, audit.Action);
        Assert.IsType<MerchantUserRegistrationSubmitted>(Assert.Single(ctx.Outbox.Enqueued));
    }

    [Fact]
    public async Task A_correction_ticket_for_a_non_rejected_account_is_refused_and_emits_no_event()
    {
        var ctx = new Ctx();
        var active = User.Register("google", "g-sub-1", "p@org.com", Now);
        active.SetDetails("Name", "User", IdentityType.Individual, null, null, null, null);
        active.Approve(Guid.NewGuid(), Now); // -> Active
        ctx.Users.Seed(active);

        var cmd = RegistrationCommand() with { Purpose = TicketPurpose.Correction };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Handler.Handle(cmd, default).AsTask());
        Assert.Empty(ctx.Outbox.Enqueued);
    }

    [Fact]
    public async Task A_registration_captures_attempt_1_with_form_fields_and_the_ticket_email()
    {
        var ctx = new Ctx();
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xE0];
        var cmd = RegistrationCommand(firstName: "Acme", lastName: "Co") with
        {
            Form = new RegistrationForm("Acme", "Co", IdentityType.Individual, "1234567890123", "PC-1", "LN-9", "0812345678"),
            PhotoBytes = bytes,
            PhotoContentType = PhotoValidation.Jpeg,
        };

        await ctx.Handler.Handle(cmd, default);

        var account = Assert.Single(ctx.Users.Added);
        var attempt = Assert.Single(ctx.Attempts.Added);
        Assert.Equal(account.Id, attempt.UserId);         // bound to the user (REQ-1.3)
        Assert.Equal(1, attempt.AttemptNo);                       // sequence starts at 1 (REQ-1.4)
        Assert.Equal(TicketPurpose.Registration, attempt.Purpose);
        Assert.Equal("Acme", attempt.FirstName);
        Assert.Equal("Co", attempt.LastName);
        Assert.Equal(IdentityType.Individual, attempt.IdentityType);
        Assert.Equal("1234567890123", attempt.IdentityNumber);
        Assert.Equal("PC-1", attempt.SaleCode);
        Assert.Equal("LN-9", attempt.LicenseNumber);
        Assert.Equal("0812345678", attempt.Phone);
        Assert.Equal("p@org.com", attempt.Email);                 // ticket email, not the account's (REQ-1.2/A3)
        Assert.Equal(ctx.Photos.ReturnedKey, attempt.PhotoObjectKey); // reference only, no byte copy (REQ-1.6)
        Assert.Equal(PhotoValidation.Jpeg, attempt.PhotoContentType);
        Assert.Equal(Now, attempt.SubmittedAt);
    }

    [Fact]
    public async Task A_correction_captures_the_next_attempt_number_on_the_same_user()
    {
        var ctx = new Ctx();
        var existing = User.Register("google", "g-sub-1", "p@org.com", Now);
        existing.SetDetails("Old", "Name", IdentityType.Individual, null, null, null, null);
        ctx.Users.Seed(existing);
        // Attempt 1 already captured by the original registration.
        ctx.Attempts.Add(RegistrationAttempt.Capture(existing, 1, TicketPurpose.Registration, "p@org.com", Now));
        existing.Reject(Now);

        var cmd = RegistrationCommand(firstName: "New", lastName: "Name") with
        {
            Purpose = TicketPurpose.Correction,
            Email = "new@org.com",
        };
        await ctx.Handler.Handle(cmd, default);

        Assert.Equal(2, ctx.Attempts.Added.Count);
        var attempt = ctx.Attempts.Added[1];
        Assert.Equal(existing.Id, attempt.UserId);        // same user carries the whole history
        Assert.Equal(2, attempt.AttemptNo);                       // 1 → 2 (REQ-1.4)
        Assert.Equal(TicketPurpose.Correction, attempt.Purpose);
        Assert.Equal("New", attempt.FirstName);
        Assert.Equal("new@org.com", attempt.Email);               // THIS attempt's ticket email
    }

    [Fact]
    public async Task A_failing_attempt_write_fails_the_whole_submit()
    {
        // Smoke only: the exception escapes the handler so ExecuteInTransactionAsync never commits (REQ-1.8);
        // real DB atomicity is proven in the lifecycle/integration suites.
        var ctx = new Ctx();
        ctx.Attempts.ThrowOnNext = new InvalidOperationException("attempt write failed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Handler.Handle(RegistrationCommand(), default).AsTask());
        Assert.Empty(ctx.Outbox.Enqueued);
    }

    [Fact]
    public async Task An_invitation_registration_binds_the_user_and_consumes_the_invitation_atomically()
    {
        var ctx = new Ctx();
        var merchantId = Guid.NewGuid();
        var invitation = MerchantUserInvitation.Create(
            merchantId, "p@org.com", InvitationTokens.Hash(InvitationTokens.New()),
            Now.AddHours(1), Guid.NewGuid(), Now);
        ctx.Invitations.Seed(invitation);

        var result = await ctx.Handler.Handle(
            RegistrationCommand() with { InvitationId = invitation.Id }, default);

        var account = Assert.Single(ctx.Users.Added);
        Assert.Equal(merchantId, account.MerchantId);
        Assert.Equal(account.Id, invitation.AcceptedByUserId);
        Assert.Equal(Now, invitation.AcceptedAt);
        var audit = Assert.Single(ctx.ManagementAudits.Appended);
        Assert.Equal(MerchantUserManagementAudit.Actions.InviteAccept, audit.Action);
        Assert.Equal(invitation.Id, audit.InvitationId);
        Assert.Equal(result.UserId, audit.TargetUserId);
    }

    [Fact]
    public async Task An_already_consumed_invitation_returns_the_stable_invitation_used_conflict()
    {
        var ctx = new Ctx();
        var invitation = MerchantUserInvitation.Create(
            Guid.NewGuid(), "p@org.com", InvitationTokens.Hash(InvitationTokens.New()),
            Now.AddHours(1), Guid.NewGuid(), Now);
        invitation.Accept(Guid.NewGuid(), "p@org.com", Now);
        ctx.Invitations.Seed(invitation);

        var exception = await Assert.ThrowsAsync<ConflictException>(() => ctx.Handler.Handle(
            RegistrationCommand() with { InvitationId = invitation.Id }, default).AsTask());

        Assert.Equal("invitation-used", exception.Code);
        Assert.Empty(ctx.Users.Added);
    }

    private static SubmitRegistrationCommand RegistrationCommand(string firstName = "Acme", string lastName = "Co") => new(
        Subject: "g-sub-1",
        Email: "p@org.com",
        HostedDomain: "org.com",
        Purpose: TicketPurpose.Registration,
        Form: new RegistrationForm(firstName, lastName, IdentityType.Individual),
        PhotoBytes: null,
        PhotoContentType: null,
        CorrelationId: "corr-1");

    // --- fakes ---

    private sealed class Ctx
    {
        public FakeMerchantUsers Users { get; } = new();
        public FakeExternalLogins Logins { get; } = new();
        public FakeAudits Audits { get; } = new();
        public FakeAttempts Attempts { get; } = new();
        public FakeOutbox Outbox { get; } = new();
        public FakeUow Uow { get; } = new();
        public FakePhotos Photos { get; } = new();
        public FakeInvitations Invitations { get; } = new();
        public FakeManagementAudits ManagementAudits { get; } = new();
        public SubmitRegistrationHandler Handler { get; }

        public Ctx() => Handler = new SubmitRegistrationHandler(
            Users, Logins, Audits, Attempts, Outbox, Uow, Photos,
            Invitations, ManagementAudits, new FakeClock(Now));
    }

    private sealed class FakeClock(DateTime now) : IClock { public DateTime UtcNow => now; }

    private sealed class FakeMerchantUsers : IAccountStore
    {
        public List<User> Added { get; } = [];
        private readonly Dictionary<string, User> _bySubject = [];
        public void Seed(User u) => _bySubject[u.Subject] = u;
        public Task<User?> FindBySubjectAsync(string provider, string subject, CancellationToken ct) =>
            Task.FromResult(_bySubject.GetValueOrDefault(subject));
        public void Add(User account) { Added.Add(account); _bySubject[account.Subject] = account; }
    }

    private sealed class FakeExternalLogins : IExternalLoginRepository
    {
        public List<ExternalLogin> Added { get; } = [];
        public void Add(ExternalLogin login) => Added.Add(login);
    }

    private sealed class FakeAudits : IRegistrationAuditWriter
    {
        public List<RegistrationAudit> Appended { get; } = [];
        public void Append(RegistrationAudit audit) => Appended.Add(audit);
    }

    private sealed class FakeAttempts : IRegistrationAttemptWriter
    {
        public List<RegistrationAttempt> Added { get; } = [];
        public Exception? ThrowOnNext { get; set; }
        public Task<int> NextAttemptNoAsync(Guid merchantUserId, CancellationToken ct) =>
            ThrowOnNext is null
                ? Task.FromResult(Added.Where(a => a.UserId == merchantUserId)
                    .Select(a => a.AttemptNo).DefaultIfEmpty(0).Max() + 1)
                : Task.FromException<int>(ThrowOnNext);
        public void Add(RegistrationAttempt attempt) => Added.Add(attempt);
    }

    private sealed class FakeOutbox : IRegistrationOutboxWriter
    {
        public List<INotification> Enqueued { get; } = [];
        public void Enqueue(INotification notification) => Enqueued.Add(notification);
    }

    private sealed class FakeInvitations : IInvitationRepository
    {
        private readonly Dictionary<Guid, MerchantUserInvitation> _items = [];
        public void Seed(MerchantUserInvitation invitation) => _items[invitation.Id] = invitation;
        public Task<MerchantUserInvitation?> FindByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_items.GetValueOrDefault(id));
        public Task<MerchantUserInvitation?> FindPendingByNormalizedEmailAsync(string email, CancellationToken ct) =>
            Task.FromResult(_items.Values.FirstOrDefault(x => x.NormalizedEmail == email));
        public Task<MerchantUserInvitation?> FindByTokenHashUnfilteredAsync(string hash, CancellationToken ct) =>
            Task.FromResult(_items.Values.FirstOrDefault(x => x.TokenHash == hash));
        public Task<MerchantUserInvitation?> FindByIdUnfilteredAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_items.GetValueOrDefault(id));
        public void Add(MerchantUserInvitation invitation) => Seed(invitation);
    }

    private sealed class FakeManagementAudits : IManagementAuditWriter
    {
        public List<MerchantUserManagementAudit> Appended { get; } = [];
        public void Append(MerchantUserManagementAudit audit) => Appended.Add(audit);
    }

    private sealed class FakeUow : IRegistrationUnitOfWork
    {
        public int SaveCalls { get; private set; }
        public Exception? SaveException { get; set; }
        public Task<int> SaveChangesAsync(CancellationToken ct)
        {
            SaveCalls++;
            return SaveException is null ? Task.FromResult(1) : Task.FromException<int>(SaveException);
        }
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            operation(ct);
    }

    private sealed class FakePhotos : IPhotoStore
    {
        public byte[]? PutBytes { get; private set; }
        public string? PutContentType { get; private set; }
        public string ReturnedKey { get; } = "deadbeefdeadbeefdeadbeefdeadbeef.jpg";
        public string ReturnedStagedKey { get; } = "feedfacefeedfacefeedfacefeedface.png";
        public bool ReturnedStagedCreatedNew { get; set; } = true;
        public Guid? StagedOperationId { get; private set; }
        public byte[]? StagedBytes { get; private set; }
        public string? StagedContentType { get; private set; }
        public List<string> CommittedKeys { get; } = [];
        public List<string> DiscardedStagedKeys { get; } = [];
        public List<string> DeletedKeys { get; } = [];
        public Task<string> PutAsync(byte[] bytes, string contentType, CancellationToken ct)
        {
            PutBytes = bytes; PutContentType = contentType;
            return Task.FromResult(ReturnedKey);
        }
        public Task<(byte[] Bytes, string ContentType)?> GetAsync(string objectKey, CancellationToken ct) =>
            Task.FromResult<(byte[], string)?>(null);
        public Task<(string Key, bool CreatedNew)> PutStagedAsync(Guid operationId, ReadOnlyMemory<byte> bytes,
            string contentType, CancellationToken ct)
        {
            StagedOperationId = operationId;
            StagedBytes = bytes.ToArray();
            StagedContentType = contentType;
            return Task.FromResult((ReturnedStagedKey, ReturnedStagedCreatedNew));
        }
        public Task CommitAsync(string objectKey, CancellationToken ct)
        {
            CommittedKeys.Add(objectKey);
            return Task.CompletedTask;
        }
        public Task DiscardStagedAsync(string objectKey, CancellationToken ct)
        {
            DiscardedStagedKeys.Add(objectKey);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string objectKey, CancellationToken ct)
        {
            DeletedKeys.Add(objectKey);
            return Task.CompletedTask;
        }
    }
}
