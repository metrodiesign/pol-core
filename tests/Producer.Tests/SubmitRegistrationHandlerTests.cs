using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Producer.Application;
using Producer.Domain;

namespace Producer.Tests;

/// <summary>
/// The <see cref="SubmitRegistrationHandler"/> orchestration (REQ-3/4/5/7/20/21): a valid Registration ticket
/// creates a Pending user + external login + profile and enqueues an audit + a registration event; a failed ticket
/// consume creates nothing; a Correction ticket resubmits the EXISTING rejected user (no second login) and re-enqueues
/// the event; identity is taken from the ticket, never the form. Persistence is faked — DB-level atomicity and the
/// unique-violation→409 translation are proven in the integration suite.
/// </summary>
public sealed class SubmitRegistrationHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_valid_registration_ticket_creates_pending_user_login_profile_audit_and_event()
    {
        var ctx = new Ctx();
        var cmd = RegistrationCommand(displayName: "Acme Co");

        var result = await ctx.Handler.Handle(cmd, default);

        var user = Assert.Single(ctx.Users.Added);
        Assert.Equal("g-sub-1", user.Subject);          // from the ticket
        Assert.Equal("p@org.com", user.Email);
        Assert.Equal(ProducerAccountStatus.PendingApproval, user.Status); // no tenant until approval (now a separate edge)
        Assert.Equal(ProducerAccountStatus.PendingApproval, result.Status);
        Assert.Equal(user.Id, result.TenantUserId);

        var login = Assert.Single(ctx.Logins.Added);
        Assert.Equal("g-sub-1", login.Subject);
        Assert.Equal(ExternalLogin.Google, login.Provider);

        var profile = Assert.Single(ctx.Profiles.Added);
        Assert.Equal("Acme Co", profile.DisplayName);
        Assert.Equal(user.Id, profile.ProducerAccountId);

        var audit = Assert.Single(ctx.Audits.Appended);
        Assert.Equal(RegistrationAuditAction.Registered, audit.Action);
        Assert.Equal("g-sub-1", audit.TargetSubject);
        Assert.Null(audit.ActorSubject);                 // self-service, no admin actor

        var evt = Assert.IsType<TenantUserRegistrationSubmitted>(Assert.Single(ctx.Outbox.Enqueued));
        Assert.Equal(user.Id, evt.TenantUserId);
        Assert.Equal("g-sub-1", evt.Subject);
        Assert.Equal("Acme Co", evt.DisplayName);
        Assert.True(ctx.Uow.SaveCalls >= 1);
    }

    [Fact]
    public async Task A_failed_ticket_consume_creates_nothing()
    {
        var ctx = new Ctx { Tickets = { ConsumeResult = false } };

        await Assert.ThrowsAsync<ArgumentException>(() => ctx.Handler.Handle(RegistrationCommand(), default).AsTask());

        Assert.Empty(ctx.Users.Added);
        Assert.Empty(ctx.Logins.Added);
        Assert.Empty(ctx.Profiles.Added);
        Assert.Empty(ctx.Audits.Appended);
        Assert.Empty(ctx.Outbox.Enqueued);
    }

    [Fact]
    public async Task A_photo_is_stored_and_its_key_is_set_on_the_profile()
    {
        var ctx = new Ctx();
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xE0];
        var cmd = RegistrationCommand() with { PhotoBytes = bytes, PhotoContentType = PhotoValidation.Jpeg };

        await ctx.Handler.Handle(cmd, default);

        Assert.Equal(bytes, ctx.Photos.PutBytes);
        Assert.Equal(PhotoValidation.Jpeg, ctx.Photos.PutContentType);
        var profile = Assert.Single(ctx.Profiles.Added);
        Assert.Equal(ctx.Photos.ReturnedKey, profile.PhotoObjectKey);
        Assert.Equal(PhotoValidation.Jpeg, profile.PhotoContentType);
    }

    [Fact]
    public async Task A_correction_ticket_resubmits_the_existing_rejected_user_without_a_second_login()
    {
        var ctx = new Ctx();
        var existing = ProducerAccount.Register("g-sub-1", "p@org.com", Now);
        existing.Reject(Now);                            // PendingApproval -> Rejected
        ctx.Users.Seed(existing);
        ctx.Profiles.Seed(TenantUserProfile.Create(existing.Id, "Old Name"));

        var cmd = RegistrationCommand(displayName: "New Name") with { Purpose = TicketPurpose.Correction };
        var result = await ctx.Handler.Handle(cmd, default);

        Assert.Empty(ctx.Users.Added);                   // edits the existing record, never a second user
        Assert.Empty(ctx.Logins.Added);                  // no second external login (REQ-5.4)
        Assert.Equal(ProducerAccountStatus.PendingApproval, existing.Status); // Rejected -> Pending
        Assert.Equal(existing.Id, result.TenantUserId);

        var profile = Assert.Single(ctx.Profiles.Seeded);
        Assert.Equal("New Name", profile.DisplayName);   // profile updated in place

        var audit = Assert.Single(ctx.Audits.Appended);
        Assert.Equal(RegistrationAuditAction.Resubmitted, audit.Action);
        Assert.IsType<TenantUserRegistrationSubmitted>(Assert.Single(ctx.Outbox.Enqueued));
    }

    [Fact]
    public async Task A_correction_ticket_for_a_non_rejected_user_is_refused_and_emits_no_event()
    {
        var ctx = new Ctx();
        var active = ProducerAccount.Register("g-sub-1", "p@org.com", Now);
        active.Approve(Now); // -> Active
        ctx.Users.Seed(active);
        ctx.Profiles.Seed(TenantUserProfile.Create(active.Id, "Name"));

        var cmd = RegistrationCommand() with { Purpose = TicketPurpose.Correction };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Handler.Handle(cmd, default).AsTask());
        Assert.Empty(ctx.Outbox.Enqueued);
    }

    private static SubmitRegistrationCommand RegistrationCommand(string displayName = "Acme Co") => new(
        TicketId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Subject: "g-sub-1",
        Email: "p@org.com",
        HostedDomain: "org.com",
        Purpose: TicketPurpose.Registration,
        Form: new RegistrationForm(displayName),
        PhotoBytes: null,
        PhotoContentType: null,
        CorrelationId: "corr-1");

    // --- fakes ---

    private sealed class Ctx
    {
        public FakeTenantUsers Users { get; } = new();
        public FakeExternalLogins Logins { get; } = new();
        public FakeTickets Tickets { get; } = new();
        public FakeProfiles Profiles { get; } = new();
        public FakeAudits Audits { get; } = new();
        public FakeOutbox Outbox { get; } = new();
        public FakeUow Uow { get; } = new();
        public FakePhotos Photos { get; } = new();
        public SubmitRegistrationHandler Handler { get; }

        public Ctx() => Handler = new SubmitRegistrationHandler(
            Users, Logins, Tickets, Profiles, Audits, Outbox, Uow, Photos, new FakeClock(Now));
    }

    private sealed class FakeClock(DateTime now) : IClock { public DateTime UtcNow => now; }

    private sealed class FakeTenantUsers : IProducerAccountRepository
    {
        public List<ProducerAccount> Added { get; } = [];
        private readonly Dictionary<string, ProducerAccount> _bySubject = [];
        public void Seed(ProducerAccount u) => _bySubject[u.Subject] = u;
        public Task<ProducerAccount?> FindBySubjectAsync(string subject, CancellationToken ct) =>
            Task.FromResult(_bySubject.GetValueOrDefault(subject));
        public Task<ProducerAccount?> FindByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_bySubject.Values.FirstOrDefault(u => u.Id == id));
        public void Add(ProducerAccount account) { Added.Add(account); _bySubject[account.Subject] = account; }
    }

    private sealed class FakeExternalLogins : IExternalLoginRepository
    {
        public List<ExternalLogin> Added { get; } = [];
        public void Add(ExternalLogin login) => Added.Add(login);
    }

    private sealed class FakeTickets : IRegistrationTicketRepository
    {
        public List<RegistrationTicket> Added { get; } = [];
        public bool ConsumeResult { get; set; } = true;
        public void Add(RegistrationTicket ticket) => Added.Add(ticket);
        public Task<bool> HasPendingAsync(string subject, string email, DateTime now, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> TryConsumeAsync(Guid ticketId, TicketPurpose purpose, DateTime now, CancellationToken ct) =>
            Task.FromResult(ConsumeResult);
    }

    private sealed class FakeProfiles : ITenantUserProfileRepository
    {
        public List<TenantUserProfile> Added { get; } = [];
        public List<TenantUserProfile> Seeded { get; } = [];
        private readonly Dictionary<Guid, TenantUserProfile> _byUser = [];
        public void Seed(TenantUserProfile p) { Seeded.Add(p); _byUser[p.ProducerAccountId] = p; }
        public Task<TenantUserProfile?> FindByProducerAccountIdAsync(Guid producerAccountId, CancellationToken ct) =>
            Task.FromResult(_byUser.GetValueOrDefault(producerAccountId));
        public void Add(TenantUserProfile profile) { Added.Add(profile); _byUser[profile.ProducerAccountId] = profile; }
    }

    private sealed class FakeAudits : IRegistrationAuditWriter
    {
        public List<RegistrationAudit> Appended { get; } = [];
        public void Append(RegistrationAudit audit) => Appended.Add(audit);
    }

    private sealed class FakeOutbox : IProducerOutboxWriter
    {
        public List<INotification> Enqueued { get; } = [];
        public void Enqueue(INotification notification) => Enqueued.Add(notification);
    }

    private sealed class FakeUow : IProducerRegistrationUnitOfWork
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
