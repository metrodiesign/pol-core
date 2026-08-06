using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// external-sim-separate-containers REQ-3 — the two invariants that
/// <c>hippodb.dbo.Documents</c>/<c>mammothdb.dbo.Documents</c> used to prove with a SQL cross-database
/// query when both lived on the same SQL Server instance (the old single-file self-check, before the split
/// into <c>02-hippo-sim.sql</c>/<c>03-mammoth-sim.sql</c>). Now that each runs on its own instance, a
/// 3-part-name JOIN across them needs a
/// linked server — these tests open two ordinary connections instead and compare in memory.
/// <para>Connections use <c>sa</c> (<see cref="IntegrationDb.SaForCatalog"/>) rather than each side's own
/// principal — these two invariants are properties of the seed, not of what a runtime principal may
/// see.</para>
/// Tagged Integration: the default unit run skips these; CI runs them against the live hippo/mammoth SQL
/// services.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SimSeedCollection.Name)]   // scheduling only — must not read dbo.Documents mid-replay (REQ-5.5)
public sealed class SimCrossInstanceConsistencyTests
{
    [Fact]
    public async Task No_DocumentNo_is_shared_between_hippodb_and_mammothdb()
    {
        var hippoNos = await DocumentNumbersAsync("hippodb");
        var mammothNos = await DocumentNumbersAsync("mammothdb");

        // Sanity: both sides actually seeded (external-sim-separate-containers REQ-5.2) — otherwise an
        // empty query on either side would make the intersection below trivially (and meaninglessly) empty.
        Assert.Equal(200, hippoNos.Count);
        Assert.Equal(200, mammothNos.Count);

        var overlap = hippoNos.Intersect(mammothNos, StringComparer.OrdinalIgnoreCase).ToArray();

        Assert.Empty(overlap);
    }

    [Fact]
    public async Task An_agent_present_on_both_sides_has_the_same_identity_on_both_sides()
    {
        var hippoRoster = await RosterAsync("hippodb");
        var mammothRoster = await RosterAsync("mammothdb");

        var sharedCodes = hippoRoster.Keys.Intersect(mammothRoster.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        // The shared 6-agent master roster (external-sim-shared-agent-network) must actually overlap on
        // both sides — an empty intersection would make the loop below vacuously true.
        Assert.Equal(6, sharedCodes.Length);

        foreach (var saleCode in sharedCodes)
        {
            var hippoAgent = hippoRoster[saleCode];
            var mammothAgent = mammothRoster[saleCode];

            // AgentIdentity is a record: field-by-field == is already null-safe (null == null on any
            // nullable string field), mirroring the EXCEPT-based SQL self-check the old cross-database
            // check used — a plain <> chain under ANSI_NULLS ON would go UNKNOWN, not TRUE, the moment
            // either side has a NULL.
            Assert.True(hippoAgent == mammothAgent,
                $"SaleCode {saleCode} identity drifted between hippodb and mammothdb: " +
                $"hippodb={hippoAgent}, mammothdb={mammothAgent}");
        }
    }

    // ---------------------------------------------------------------- plumbing

    private static async Task<HashSet<string>> DocumentNumbersAsync(string catalog)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaForCatalog(catalog));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DocumentNo FROM dbo.Documents WHERE DocumentNo IS NOT NULL";

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private sealed record AgentIdentity(
        string? SaleFullName, string? BrokerCode, string? BrokerName, string? ReferenceBranch, string? PolicyBranch);

    private static async Task<Dictionary<string, AgentIdentity>> RosterAsync(string catalog)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaForCatalog(catalog));
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT SaleCode, SaleFullName, BrokerCode, BrokerName, ReferenceBranch, PolicyBranch " +
            "FROM dbo.Documents";

        var result = new Dictionary<string, AgentIdentity>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var saleCode = reader.GetString(0);
            result[saleCode] = new AgentIdentity(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }
        return result;
    }
}
