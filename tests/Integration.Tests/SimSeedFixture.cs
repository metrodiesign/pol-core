using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Serialises every class that opens a connection to a sim instance (hippodb/mammothdb) behind one
/// <see cref="SimSeedFixture"/>: the fixture may replay a whole bootstrap script (DELETE + re-INSERT
/// of dbo.Documents), and xUnit's default per-class parallelism would let another class read mid-wipe.
/// Same precedent as <see cref="IamCatalogCollection"/>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SimSeedCollection : ICollectionFixture<SimSeedFixture>
{
    public const string Name = "sim-seed";
}

/// <summary>
/// sim-seed-date-stability — makes the sim suites independent of container age and host timezone
/// (REQ-1). The seed data is computed once, relative to the bootstrap day's GETDATE(); the SPs'
/// search window moves with every call; and the tests used to expect values from the HOST's clock.
/// Three clocks, one of them frozen — after a day apart, red tests with empty diffs.
///
/// The fix: "today" is measured on the SIM's own clock only. Each bootstrap script records its
/// @today into dbo.SeedInfo (the anchor) in the same batch that seeds the data; this fixture
/// compares that anchor against the sim's own CAST(GETDATE() AS date) and, when stale (old
/// container) or missing (pre-SeedInfo volume), replays the REAL bootstrap script — the same file,
/// the same self-checks, no second seed implementation (REQ-4.4: plain `dotnet test` heals itself;
/// no manual restart or down -v). Tests then read <see cref="Anchor"/> instead of the host's today,
/// which removes the host clock from the equation entirely (REQ-2.3).
/// </summary>
public sealed class SimSeedFixture : IAsyncLifetime
{
    private sealed record Sim(string Catalog, string ScriptFile, string PasswordVariable, string PasswordEnv);

    private static readonly Sim[] Sims =
    [
        new("hippodb", "02-hippo-sim.sql", "$(HIPPO_APP_PASSWORD)", "POL_HIPPO_APP_PASSWORD"),
        new("mammothdb", "03-mammoth-sim.sql", "$(MAMMOTH_APP_PASSWORD)", "POL_MAMMOTH_APP_PASSWORD"),
    ];

    /// <summary>The verified seed anchor — the "today" every date-relative expectation must be
    /// computed from (in place of the host's own clock). A date at midnight; both sim instances agreed on
    /// it during <see cref="InitializeAsync"/>.</summary>
    public DateTime Anchor { get; private set; }

    public async Task InitializeAsync()
    {
        var anchors = new DateTime[Sims.Length];
        for (var i = 0; i < Sims.Length; i++)
            anchors[i] = await EnsureFreshAsync(Sims[i]);

        // REQ-1.4 shape: never pick a side silently — name both values.
        if (anchors[0] != anchors[1])
            throw new InvalidOperationException(
                $"The two sim instances disagree on today after refresh: hippodb anchor "
                + $"{anchors[0]:yyyy-MM-dd}, mammothdb anchor {anchors[1]:yyyy-MM-dd}. "
                + "Check the containers' clocks/TZ.");
        Anchor = anchors[0];
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>REQ-1.4 — call on a sim connection right before EXECing a search procedure. If UTC
    /// midnight crossed after the fixture verified the anchor (the SP would now use a different
    /// "today" than the data), fail with a message naming both values instead of letting an
    /// `Expected 42 Actual 41` surface with no cause attached.</summary>
    public async Task GuardAnchorAsync(SqlConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        // One query, one clock: the anchor and the sim's today come from the same instant.
        cmd.CommandText = "SELECT AnchorDate, CAST(GETDATE() AS date) FROM dbo.SeedInfo;";
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(
                "dbo.SeedInfo is empty — the sim seed did not record its anchor.");
        var anchor = reader.GetDateTime(0);
        var today = reader.GetDateTime(1);
        if (anchor != today)
            throw new InvalidOperationException(
                $"Sim seed anchor {anchor:yyyy-MM-dd} no longer matches the sim's own today "
                + $"{today:yyyy-MM-dd} — midnight crossed mid-suite (or the seed was replaced "
                + "underneath the run). Re-run the suite; the fixture will refresh the seed.");
    }

    /// <summary>Returns the verified anchor for one sim, replaying its bootstrap script when the
    /// anchor is missing or stale. Two attempts: a replay started just before UTC midnight can
    /// legitimately come out stale once.</summary>
    private static async Task<DateTime> EnsureFreshAsync(Sim sim)
    {
        // Database=master: a volume that predates SeedInfo — or a fresh instance where the catalog
        // does not exist yet — must still be connectable; the script carries its own USE/CREATE.
        var master = new SqlConnectionStringBuilder(IntegrationDb.SaForCatalog(sim.Catalog))
        {
            InitialCatalog = "master",
        }.ConnectionString;
        await using var connection = await IntegrationDb.OpenAsync(master);

        var (anchor, today) = await ReadAnchorAsync(connection, sim.Catalog);
        for (var replays = 0; anchor != today && replays < 2; replays++)
        {
            await ReplayAsync(connection, sim);
            (anchor, today) = await ReadAnchorAsync(connection, sim.Catalog);
        }
        if (anchor != today)
            throw new InvalidOperationException(
                $"{sim.Catalog} anchor is {(anchor is null ? "missing" : anchor.Value.ToString("yyyy-MM-dd"))} "
                + $"but the sim's own today is {today:yyyy-MM-dd}, even after replaying {sim.ScriptFile} "
                + "twice. Check the container's clock/TZ.");
        return today;
    }

    /// <summary>Reads (anchor, sim-today). Anchor is null when the catalog, the SeedInfo table, or
    /// its row does not exist yet — all three just mean "stale, replay".</summary>
    private static async Task<(DateTime? Anchor, DateTime Today)> ReadAnchorAsync(SqlConnection connection, string catalog)
    {
        if (await IntegrationDb.ScalarAsync(connection, $"SELECT OBJECT_ID(N'{catalog}.dbo.SeedInfo', N'U');") is null or DBNull)
        {
            var today = (DateTime)(await IntegrationDb.ScalarAsync(connection, "SELECT CAST(GETDATE() AS date);"))!;
            return (null, today);
        }

        await using var cmd = connection.CreateCommand();
        // One query so both values come from the same instant on the sim's clock — no host time involved.
        cmd.CommandText = $"SELECT AnchorDate, CAST(GETDATE() AS date) FROM [{catalog}].dbo.SeedInfo;";
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            var today = (DateTime)(await IntegrationDb.ScalarAsync(connection, "SELECT CAST(GETDATE() AS date);"))!;
            return (null, today);
        }
        return (reader.GetDateTime(0), reader.GetDateTime(1));
    }

    /// <summary>Replays the sim's REAL bootstrap script — read from docker/bootstrap/, password
    /// variable substituted from the same env var <see cref="IntegrationDb.ForCatalog"/> requires,
    /// split on GO — exactly what the bootstrap container runs. Its self-checks THROW as
    /// SqlException on any drift, which fails every test in the collection with the script's own
    /// message.</summary>
    private static async Task ReplayAsync(SqlConnection connection, Sim sim)
    {
        var script = (await File.ReadAllTextAsync(SqlScripts.RepoPath("docker", "bootstrap", sim.ScriptFile)))
            .Replace(sim.PasswordVariable, IntegrationDb.Require(sim.PasswordEnv), StringComparison.Ordinal);
        foreach (var batch in SqlScripts.SplitBatches(script))
            await IntegrationDb.ExecAsync(connection, batch);
    }
}
