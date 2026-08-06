namespace Integration.Tests;

/// <summary>
/// products-external-source-of-truth REQ-2.7: SQL owns the "same document" rule, so every <c>DocumentNo</c>
/// column in VCentralPay has to compare the same way — case-insensitive and Thai-aware. Nothing in <c>src</c>
/// sets a per-column collation (design decision #5 forbids it: a single <c>HasCollation</c> would make joins
/// across these columns fail with error 468), which is exactly why this needs proving against the live
/// database rather than the model: the guarantee comes from the database's own default, and a database
/// created with the wrong one would break the rule silently everywhere at once.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DocumentNoCollationIntegrationTests
{
    [Fact]
    public async Task Every_DocumentNo_column_shares_one_case_insensitive_collation()
    {
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            SELECT CONCAT(s.name, '.', t.name) AS TableName, c.collation_name
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE c.name = 'DocumentNo'
            ORDER BY TableName;
            """;

        var columns = new List<(string Table, string Collation)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                columns.Add((reader.GetString(0), reader.GetString(1)));

        Assert.True(columns.Count >= 2,
            "Expected DocumentNo on at least shop.OrderItems and shop.CheckoutSessionItems — is the database migrated? "
            + $"Found: {columns.Count}");

        var distinct = columns.Select(x => x.Collation).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(distinct.Count == 1,
            "DocumentNo columns disagree on collation, so an equality that holds in one table can fail in another "
            + "(and joining them raises SQL error 468): "
            + string.Join(", ", columns.Select(x => $"{x.Table}={x.Collation}")));

        Assert.Contains("_CI", distinct[0], StringComparison.OrdinalIgnoreCase);
    }
}
