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
        Assert.Equal(account.Id, result.MerchantUserId);

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
        Assert.Equal(account.Id, evt.MerchantUserId);
        Assert.Equal("g-sub-1", evt.Subject);
        Assert.Equal("Acme Co", evt.DisplayName);
        Assert.True(ctx.Uow.SaveCalls >= 1);
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
    public async Task A_correction_ticket_resubmits_the_existing_rejected_account_without_a_second_login()
    {
        var ctx = new Ctx();
        var existing = User.Register("g-sub-1", "p@org.com", Now);
        existing.SetDetails("Old", "Name", null, null, null, null, null);
        existing.Reject(Now);                            // PendingApproval -> Rejected
        ctx.Users.Seed(existing);

        var cmd = RegistrationCommand(firstName: "New", lastName: "Name") with { Purpose = TicketPurpose.Correction };
        var result = await ctx.Handler.Handle(cmd, default);

        Assert.Empty(ctx.Users.Added);                   // edits the existing record, never a second account
        Assert.Empty(ctx.Logins.Added);                  // no second external login (REQ-5.4)
        Assert.Equal(UserStatus.PendingApproval, existing.Status); // Rejected -> Pending
        Assert.Equal(existing.Id, result.MerchantUserId);
        Assert.Equal("New Name", existing.DisplayName);  // account updated in place (computed from first + last)

        var audit = Assert.Single(ctx.Audits.Appended);
        Assert.Equal(RegistrationAuditAction.Resubmitted, audit.Action);
        Assert.IsType<MerchantUserRegistrationSubmitted>(Assert.Single(ctx.Outbox.Enqueued));
    }

    [Fact]
    public async Task A_correction_ticket_for_a_non_rejected_account_is_refused_and_emits_no_event()
    {
        var ctx = new Ctx();
        var active = User.Register("g-sub-1", "p@org.com", Now);
        active.SetDetails("Name", "User", null, null, null, null, null);
        active.Approve(Guid.NewGuid(), Now); // -> Active
        ctx.Users.Seed(active);

        var cmd = RegistrationCommand() with { Purpose = TicketPurpose.Correction };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Handler.Handle(cmd, default).AsTask());
        Assert.Empty(ctx.Outbox.Enqueued);
    }

    private static SubmitRegistrationCommand RegistrationCommand(string firstName = "Acme", string lastName = "Co") => new(
        Subject: "g-sub-1",
        Email: "p@org.com",
        HostedDomain: "org.com",
        Purpose: TicketPurpose.Registration,
        Form: new RegistrationForm(firstName, lastName),
        PhotoBytes: null,
        PhotoContentType: null,
        CorrelationId: "corr-1");

    // --- fakes ---

    private sealed class Ctx
    {
        public FakeMerchantUsers Users { get; } = new();
        public FakeExternalLogins Logins { get; } = new();
        public FakeAudits Audits { get; } = new();
        public FakeOutbox Outbox { get; } = new();
        public FakeUow Uow { get; } = new();
        public FakePhotos Photos { get; } = new();
        public SubmitRegistrationHandler Handler { get; }

        public Ctx() => Handler = new SubmitRegistrationHandler(
            Users, Logins, Audits, Outbox, Uow, Photos, new FakeClock(Now));
    }

    private sealed class FakeClock(DateTime now) : IClock { public DateTime UtcNow => now; }

    private sealed class FakeMerchantUsers : IUserRepository
    {
        public List<User> Added { get; } = [];
        private readonly Dictionary<string, User> _bySubject = [];
        public void Seed(User u) => _bySubject[u.Subject] = u;
        public Task<User?> FindBySubjectAsync(string subject, CancellationToken ct) =>
            Task.FromResult(_bySubject.GetValueOrDefault(subject));
        public Task<User?> FindByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_bySubject.Values.FirstOrDefault(u => u.Id == id));
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

    private sealed class FakeOutbox : IRegistrationOutboxWriter
    {
        public List<INotification> Enqueued { get; } = [];
        public void Enqueue(INotification notification) => Enqueued.Add(notification);
    }

    private sealed class FakeUow : IRegistrationUnitOfWork
    {
        public int SaveCalls { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken ct) { SaveCalls++; return Task.FromResult(1); }
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            operation(ct);
    }

    private sealed class FakePhotos : IPhotoStore
    {
        public byte[]? PutBytes { get; private set; }
        public string? PutContentType { get; private set; }
        public string ReturnedKey { get; } = "deadbeefdeadbeefdeadbeefdeadbeef.jpg";
        public Task<string> PutAsync(byte[] bytes, string contentType, CancellationToken ct)
        {
            PutBytes = bytes; PutContentType = contentType;
            return Task.FromResult(ReturnedKey);
        }
        public Task<(byte[] Bytes, string ContentType)?> GetAsync(string objectKey, CancellationToken ct) =>
            Task.FromResult<(byte[], string)?>(null);
    }
}
