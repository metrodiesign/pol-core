using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantUsers;
using Persistence.MerchantUsers.Users;
using MerchantUserAccount = Merchants.Domain.Users.User;

namespace Architecture.Tests;

/// <summary>
/// Proves the rls-to-query-filter task 5 approve/reject write ports (design.md "Pre-owner-bind READS vs
/// WRITES" — <see cref="IApproveRegistrationWriter"/> / <see cref="IRejectRegistrationWriter"/>, R1-v7 #1):
/// the conditional DML performs the one-time NULL-&gt;merchant transition exactly on a PENDING row matched by
/// verified Subject, is idempotent for a same-merchant replay, rejects a different-merchant replay without
/// mutating the stored row, and — like the read ports — genuinely needs <c>IgnoreQueryFilters()</c> because an
/// admin-driven call has no merchant actor bound (<c>CurrentMerchant</c> resolves to <see cref="Guid.Empty"/>,
/// which never matches a NULL <c>MerchantId</c> row either).
/// </summary>
public sealed class MerchantRegistrationWriterTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection;

    public MerchantRegistrationWriterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    private MerchantUserDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(_connection).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private async Task SeedPendingAsync(string subject, string email)
    {
        using var writer = NewContext();
        writer.Users.Add(MerchantUserAccount.Register(subject, email, DateTime.UtcNow));
        await writer.SaveChangesAsync();
    }

    private async Task<MerchantUserAccount> LoadAsync(string subject)
    {
        using var reader = NewContext();
        return await reader.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Subject == subject);
    }

    [Fact]
    public async Task Approve_binds_a_pending_user_to_the_target_merchant()
    {
        await SeedPendingAsync("m-sub-1", "one@example.com");

        using var writer = NewContext();
        var outcome = await new MerchantRegistrationWriter(writer, NoOpSecurityTelemetry.Instance).ApproveAsync("m-sub-1", MerchantA, CancellationToken.None);

        Assert.Equal(ApproveRegistrationOutcome.Approved, outcome);
        var row = await LoadAsync("m-sub-1");
        Assert.Equal(MerchantA, row.MerchantId);
        Assert.Equal(Merchants.Domain.Users.UserStatus.Active, row.Status);
    }

    [Fact]
    public async Task Approve_without_an_actor_bound_still_finds_the_pending_row_only_via_the_port()
    {
        // The mechanism this port exists for: an unbound caller's ordinary filtered query (CurrentMerchant ==
        // Guid.Empty) never matches a NULL MerchantId row, so without IgnoreQueryFilters the port itself would
        // be unable to see what it needs to approve.
        await SeedPendingAsync("m-sub-2", "two@example.com");

        using var filtered = NewContext();
        Assert.Equal(0, await filtered.Users.CountAsync());

        using var writer = NewContext();
        var outcome = await new MerchantRegistrationWriter(writer, NoOpSecurityTelemetry.Instance).ApproveAsync("m-sub-2", MerchantA, CancellationToken.None);
        Assert.Equal(ApproveRegistrationOutcome.Approved, outcome);
    }

    [Fact]
    public async Task Approve_is_idempotent_when_replayed_for_the_same_target_merchant()
    {
        await SeedPendingAsync("m-sub-3", "three@example.com");
        using (var first = NewContext())
            await new MerchantRegistrationWriter(first, NoOpSecurityTelemetry.Instance).ApproveAsync("m-sub-3", MerchantA, CancellationToken.None);

        using var replay = NewContext();
        var outcome = await new MerchantRegistrationWriter(replay, NoOpSecurityTelemetry.Instance).ApproveAsync("m-sub-3", MerchantA, CancellationToken.None);

        Assert.Equal(ApproveRegistrationOutcome.AlreadyApprovedSameMerchant, outcome);
        var row = await LoadAsync("m-sub-3");
        Assert.Equal(MerchantA, row.MerchantId); // unchanged
    }

    [Fact]
    public async Task Approve_rejects_a_replay_for_a_different_target_merchant_without_mutating_the_row()
    {
        await SeedPendingAsync("m-sub-4", "four@example.com");
        using (var first = NewContext())
            await new MerchantRegistrationWriter(first, NoOpSecurityTelemetry.Instance).ApproveAsync("m-sub-4", MerchantA, CancellationToken.None);

        using var replay = NewContext();
        var outcome = await new MerchantRegistrationWriter(replay, NoOpSecurityTelemetry.Instance).ApproveAsync("m-sub-4", MerchantB, CancellationToken.None);

        Assert.Equal(ApproveRegistrationOutcome.AlreadyApprovedDifferentMerchant, outcome);
        var row = await LoadAsync("m-sub-4");
        Assert.Equal(MerchantA, row.MerchantId); // still the original merchant, never overwritten to B
    }

    [Fact]
    public async Task Approve_returns_NotFound_for_an_unknown_subject()
    {
        using var writer = NewContext();
        var outcome = await new MerchantRegistrationWriter(writer, NoOpSecurityTelemetry.Instance).ApproveAsync("no-such-subject", MerchantA, CancellationToken.None);

        Assert.Equal(ApproveRegistrationOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task Approve_returns_NotApprovable_for_an_already_rejected_user()
    {
        await SeedPendingAsync("m-sub-5", "five@example.com");
        using (var reject = NewContext())
            await new MerchantRegistrationWriter(reject, NoOpSecurityTelemetry.Instance).RejectAsync("m-sub-5", CancellationToken.None);

        using var writer = NewContext();
        var outcome = await new MerchantRegistrationWriter(writer, NoOpSecurityTelemetry.Instance).ApproveAsync("m-sub-5", MerchantA, CancellationToken.None);

        Assert.Equal(ApproveRegistrationOutcome.NotApprovable, outcome);
    }

    [Fact]
    public async Task Reject_transitions_a_pending_user_to_rejected()
    {
        await SeedPendingAsync("m-sub-6", "six@example.com");

        using var writer = NewContext();
        var outcome = await new MerchantRegistrationWriter(writer, NoOpSecurityTelemetry.Instance).RejectAsync("m-sub-6", CancellationToken.None);

        Assert.Equal(RejectRegistrationOutcome.Rejected, outcome);
        var row = await LoadAsync("m-sub-6");
        Assert.Equal(Merchants.Domain.Users.UserStatus.Rejected, row.Status);
        Assert.Null(row.MerchantId); // reject never binds a merchant
    }

    [Fact]
    public async Task Reject_returns_NotPending_when_replayed_against_an_already_rejected_user()
    {
        await SeedPendingAsync("m-sub-7", "seven@example.com");
        using (var first = NewContext())
            await new MerchantRegistrationWriter(first, NoOpSecurityTelemetry.Instance).RejectAsync("m-sub-7", CancellationToken.None);

        using var replay = NewContext();
        var outcome = await new MerchantRegistrationWriter(replay, NoOpSecurityTelemetry.Instance).RejectAsync("m-sub-7", CancellationToken.None);

        Assert.Equal(RejectRegistrationOutcome.NotPending, outcome);
    }

    [Fact]
    public async Task Reject_returns_NotFound_for_an_unknown_subject()
    {
        using var writer = NewContext();
        var outcome = await new MerchantRegistrationWriter(writer, NoOpSecurityTelemetry.Instance).RejectAsync("no-such-subject", CancellationToken.None);

        Assert.Equal(RejectRegistrationOutcome.NotFound, outcome);
    }

    public void Dispose() => _connection.Dispose();
}
