using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves the pooled connection-reuse guarantee (rf1-schema-reset T13/REQ-4.1): a stale UserId/MerchantId from a
/// PRIOR logical connection must never survive into a NEW logical <c>Open()</c> that reuses the same physical
/// connection. Every other fixture in this suite (<see cref="IntegrationDb"/>) disables pooling by design so a
/// fresh physical connection never inherits a SESSION_CONTEXT — this is the one place pooling is deliberately
/// left ON (<see cref="IntegrationDb.PooledAppConn"/>, <c>Max Pool Size=1</c> forces the SAME physical
/// connection/SPID to be handed back on the next Open), because that is exactly the scenario T13 exists to guard
/// against. Builds on the go/no-go spike (task 1, <c>docs/spikes/2026-07-11-rf1-rls-session-context.md</c>),
/// which found SqlClient's own <c>sp_reset_connection</c> already scrubs SESSION_CONTEXT on physical reuse.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PooledConnectionReuseTests
{
    // Convert (not a cast): COUNT(*) comes back as int, but @@SPID comes back as smallint.
    private static int AsInt(object? o) => Convert.ToInt32(o);

    [Fact]
    public async Task Stale_actor_context_does_not_survive_a_pooled_connection_reuse()
    {
        var merchantA = Guid.NewGuid();
        var productA = Guid.NewGuid();
        await using (var setup = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchantA))
            await IntegrationDb.InsertProductAsync(setup, productA, merchantA);

        int spidRound1;
        await using (var round1 = await IntegrationDb.OpenAsync(IntegrationDb.PooledAppConn, merchantA))
        {
            spidRound1 = AsInt(await IntegrationDb.ScalarAsync(round1, "SELECT @@SPID"));
            Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(round1,
                "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", productA))));
        } // returned to the pool here — SqlClient resets it on the NEXT checkout, not on return.

        // A brand-new logical Open() on the SAME pooled connection string, with NO re-stamp at all. If the pool
        // handed back the same physical connection AND SESSION_CONTEXT had survived, this would still see
        // MerchantA's row — a real cross-request leak. It must not: physical reuse already scrubs SESSION_CONTEXT
        // before this query runs, so an un-stamped reuse falls back to REQ-3.5's "no context = 0 rows" — a stale
        // actor cannot leak across the pool even before the interceptor gets a chance to re-stamp.
        await using var round2 = await IntegrationDb.OpenAsync(IntegrationDb.PooledAppConn);
        var spidRound2 = AsInt(await IntegrationDb.ScalarAsync(round2, "SELECT @@SPID"));
        var seenAfterReuseWithNoRestamp = AsInt(await IntegrationDb.ScalarAsync(round2,
            "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", productA)));

        Assert.Equal(spidRound1, spidRound2); // proves this really is the SAME physical connection, not a fresh one
        Assert.Equal(0, seenAfterReuseWithNoRestamp);
    }

    [Fact]
    public async Task Re_stamping_after_reuse_binds_the_new_actor_cleanly_with_no_cross_contamination()
    {
        var merchantA = Guid.NewGuid();
        var merchantB = Guid.NewGuid();
        var productA = Guid.NewGuid();
        var productB = Guid.NewGuid();
        await using (var setup = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchantA))
            await IntegrationDb.InsertProductAsync(setup, productA, merchantA);
        await using (var setup = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchantB))
            await IntegrationDb.InsertProductAsync(setup, productB, merchantB);

        int spidRound1;
        await using (var round1 = await IntegrationDb.OpenAsync(IntegrationDb.PooledAppConn, merchantA))
            spidRound1 = AsInt(await IntegrationDb.ScalarAsync(round1, "SELECT @@SPID"));

        // Reuse the same physical connection, re-stamped for a DIFFERENT merchant this time (the interceptor's
        // real-world job every request) — proves the new stamp fully replaces the old one, with no bleed-through.
        await using var round2 = await IntegrationDb.OpenAsync(IntegrationDb.PooledAppConn, merchantB);
        var spidRound2 = AsInt(await IntegrationDb.ScalarAsync(round2, "SELECT @@SPID"));

        Assert.Equal(spidRound1, spidRound2);
        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(round2, "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", productB))));
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(round2, "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", productA))));
    }
}
