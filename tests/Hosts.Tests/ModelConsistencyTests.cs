extern alias ApiHost;

using Microsoft.EntityFrameworkCore;

namespace Hosts.Tests;

// REQ-9.3 / design "Migration strategy": the four-migration chain (InitialSchema + SecurityObjects + SeedData +
// OneBasedPersistedEnumStorage) plus
// PolDbContextModelSnapshot.cs are a CUMULATIVE snapshot — if a later model change (a new iam column, a moved FK)
// is not captured by regenerating the chain, the next `migrations add` diffs against a stale snapshot and emits
// wrong DDL. This guard fails the build the moment the runtime model and the snapshot drift, so the chain can
// never silently fall behind the code (critique P2-1). HasPendingModelChanges() is an in-memory comparison of the
// model against the migrations-assembly snapshot — it opens NO connection, so this runs in the non-integration
// tier with no live SQL Server. The design-time factory builds the exact same model `dotnet ef` and the host use.
public sealed class ModelConsistencyTests
{
    [Fact]
    public void Model_has_no_pending_changes_against_the_migration_snapshot()
    {
        using var db = new ApiHost::Api.PolDbContextFactory().CreateDbContext([]);

        Assert.False(
            db.Database.HasPendingModelChanges(),
            "The EF model drifted from PolDbContextModelSnapshot. Regenerate the migration chain " +
            "(migrations remove down the chain, then re-add the current migration chain) so the " +
            "snapshot matches the model — do NOT hand-edit the snapshot.");
    }
}
