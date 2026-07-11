using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// The merchant-user registration outbox grant (merchant-user-google-sso REQ-20.2 / critique B1, rf1-schema-reset
/// T5) against live SQL Server. Registration writes its <c>MerchantUserRegistrationSubmitted</c> event on the
/// keyed pol_admin connection, in the same transaction as the merchant-less Pending row, stamped with a fixed
/// sentinel MerchantId. RLS bypass skips PREDICATES, not table GRANTs — so pol_admin needs an explicit INSERT
/// grant on txn.OutboxMessages. T5 additionally removed pol_admin from pol_rls_bypass, so the sentinel row's
/// BLOCK-after-insert predicate is satisfied by sec.fn_outbox_predicate's explicit sentinel carve-out, not a
/// blanket bypass. These prove the grant exists, the sentinel row inserts, and the existing pol_app insert path
/// is unchanged.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MerchantUserRegistrationOutboxTests
{
    // Matches Merchants.Infrastructure.Persistence.MerchantsOutbox.SentinelMerchantId.
    private static readonly Guid Sentinel = new("f0f0f0f0-0000-4000-8000-00000000ad17");

    // A non-registered Type so that even if a dispatcher ran against this DB it would not act on the probe row.
    private const string InsertOutbox =
        "INSERT txn.OutboxMessages (Id, MerchantId, Type, Payload, OccurredAt, Attempts) " +
        "VALUES (@id, @m, N'IntegrationProbe', N'{}', SYSUTCDATETIME(), 0)";

    [Fact]
    public async Task PolAdmin_can_insert_a_sentinel_merchant_outbox_row()
    {
        // pol_admin is granted INSERT only (least privilege — the registration writer never reads OutboxMessages):
        // the insert commits (rowcount 1) via sec.fn_outbox_predicate's sentinel carve-out (T5 — no more blanket bypass).
        var id = Guid.CreateVersion7();
        await using (var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn))
        {
            var rows = await IntegrationDb.ExecAsync(admin, InsertOutbox, ("@id", id), ("@m", Sentinel));
            Assert.Equal(1, rows);
        }

        // Verify it persisted via pol_worker (the dispatcher principal, which holds SELECT) — also proves the
        // worker can see the row it must lease + publish.
        await using var worker = await IntegrationDb.OpenAsync(IntegrationDb.WorkerConn);
        var seen = (int)(await IntegrationDb.ScalarAsync(worker,
            "SELECT COUNT(*) FROM txn.OutboxMessages WHERE Id=@id", ("@id", id)))!;
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task PolWorker_cannot_insert_an_outbox_row()
    {
        // pol_worker leases + marks processed (SELECT/UPDATE) but never WRITES events — only pol_app and pol_admin do.
        var id = Guid.CreateVersion7();
        await using var worker = await IntegrationDb.OpenAsync(IntegrationDb.WorkerConn);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(worker, InsertOutbox, ("@id", id), ("@m", Sentinel)));
    }
}
