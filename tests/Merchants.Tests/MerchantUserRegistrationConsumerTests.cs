using BuildingBlocks.Application;
using Contracts;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;

namespace Merchants.Tests;

/// <summary>The Admin-side outbox consumer (REQ-20.4) records one notice per registration and is idempotent under
/// at-least-once delivery: a first event records a notice; a redelivery for an already-noticed user records nothing
/// and never throws (a concurrent unique-violation is swallowed as a no-op so the dispatcher is not poisoned).</summary>
public sealed class MerchantUserRegistrationConsumerTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static MerchantUserRegistrationSubmitted Event() =>
        new(UserId, "g-sub-1", "p@org.com", "org.com", "Acme Co", Now);

    [Fact]
    public async Task A_first_event_records_a_notice()
    {
        var notices = new FakeNotices();
        var consumer = new RegistrationConsumer(notices, new FakeClock(Now));

        await consumer.Handle(Event(), default);

        var notice = Assert.Single(notices.Saved);
        Assert.Equal(UserId, notice.MerchantUserId);
        Assert.Equal("Acme Co", notice.DisplayName);
        Assert.Equal(1, notices.SaveCalls);
    }

    [Fact]
    public async Task A_redelivery_for_an_already_noticed_user_is_a_no_op()
    {
        var notices = new FakeNotices();
        notices.SeedExisting(UserId);
        var consumer = new RegistrationConsumer(notices, new FakeClock(Now));

        await consumer.Handle(Event(), default);

        Assert.Empty(notices.Saved);     // nothing added
        Assert.Equal(0, notices.SaveCalls);
    }

    [Fact]
    public async Task A_concurrent_unique_violation_is_swallowed_not_thrown()
    {
        var notices = new FakeNotices { FailNextSaveAsConflict = true };
        var consumer = new RegistrationConsumer(notices, new FakeClock(Now));

        // Exists() returned false (race), the insert lost the unique-index race -> TrySave returns false, no throw.
        await consumer.Handle(Event(), default);

        Assert.Equal(1, notices.SaveCalls);
        Assert.Empty(notices.Saved);     // the losing notice is not recorded
    }

    private sealed class FakeClock(DateTime now) : IClock { public DateTime UtcNow => now; }

    private sealed class FakeNotices : IRegistrationNoticeWriter
    {
        private readonly HashSet<Guid> _existing = [];
        private RegistrationNotice? _pending;
        public List<RegistrationNotice> Saved { get; } = [];
        public int SaveCalls { get; private set; }
        public bool FailNextSaveAsConflict { get; init; }

        public void SeedExisting(Guid merchantUserId) => _existing.Add(merchantUserId);
        public Task<bool> ExistsAsync(Guid merchantUserId, CancellationToken ct) =>
            Task.FromResult(_existing.Contains(merchantUserId));
        public void Add(RegistrationNotice notice) => _pending = notice;

        public Task<bool> TrySaveAsync(CancellationToken ct)
        {
            SaveCalls++;
            if (FailNextSaveAsConflict)
            {
                _pending = null;     // detached losing notice, as the real writer does
                return Task.FromResult(false);
            }
            if (_pending is not null)
            {
                Saved.Add(_pending);
                _existing.Add(_pending.MerchantUserId);
                _pending = null;
            }
            return Task.FromResult(true);
        }
    }
}
