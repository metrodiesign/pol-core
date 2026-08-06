using BuildingBlocks.Infrastructure.Persistence.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Integration.Tests;

/// <summary>
/// products-external-source-of-truth REQ-10.2 + edge case #7 — <c>ProducerCode</c> becomes <c>SaleCode</c>
/// WITHOUT losing a single row's value. This is the one claim in task 1 that no unit test can make: the trap
/// (hit for real in products-sp-53-alignment) is that <c>dotnet ef migrations add</c> scaffolds a rename as
/// DropColumn + AddColumn, which compiles, applies cleanly, and silently empties the column.
/// <para>The migration under test is the PRODUCTION type — its operations are rendered by EF's real SQL Server
/// generator and executed against a real SQL Server, on a scratch database holding the two columns in their
/// pre-migration shape with data in them. A regenerated DropColumn+AddColumn migration fails here on the value
/// assertions, and the narrowing to <c>varchar(20)</c> is proven by the column metadata afterwards (REQ-10.6).</para>
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SaleCodeRenameMigrationIntegrationTests
{
    // Rows that must survive verbatim: an ordinary code, one at the new 20-character ceiling, and NULL
    // (the common shape — an applicant who never supplied one).
    private static readonly (Guid Id, string? Code)[] Seeded =
    [
        (Guid.Parse("11111111-1111-4111-8111-111111111111"), "PRD-VP-001"),
        (Guid.Parse("22222222-2222-4222-8222-222222222222"), "12345678901234567890"),
        (Guid.Parse("33333333-3333-4333-8333-333333333333"), null),
    ];

    [Fact]
    public async Task Every_producer_code_value_is_still_there_under_the_new_name()
    {
        var database = $"pol_salecode_rename_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var c = await OpenScratchAsync(database);
            await CreatePreMigrationTablesAsync(c);
            foreach (var (id, code) in Seeded)
            {
                await IntegrationDb.ExecAsync(c,
                    "INSERT merch.Users (Id, ProducerCode) VALUES (@id, @code);",
                    ("@id", id), ("@code", (object?)code ?? DBNull.Value));
                await IntegrationDb.ExecAsync(c,
                    "INSERT merch.RegistrationAttempts (Id, ProducerCode) VALUES (@id, @code);",
                    ("@id", id), ("@code", (object?)code ?? DBNull.Value));
            }

            foreach (var sql in RenderUpScript())
                await IntegrationDb.ExecAsync(c, sql);

            foreach (var table in (string[])["Users", "RegistrationAttempts"])
                foreach (var (id, code) in Seeded)
                {
                    var stored = await IntegrationDb.ScalarAsync(c,
                        $"SELECT SaleCode FROM merch.{table} WHERE Id = @id;", ("@id", id));
                    Assert.Equal(code, stored is DBNull ? null : (string?)stored);
                }
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    // REQ-10.6: both columns end up in the upstream contract's shape — non-unicode, 20 wide. A varchar(20)
    // column reports max_length 20; an nvarchar(20) would report 40, so this also pins the non-unicode half.
    [Fact]
    public async Task Both_columns_end_up_as_varchar_20()
    {
        var database = $"pol_salecode_shape_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var c = await OpenScratchAsync(database);
            await CreatePreMigrationTablesAsync(c);

            foreach (var sql in RenderUpScript())
                await IntegrationDb.ExecAsync(c, sql);

            foreach (var table in (string[])["Users", "RegistrationAttempts"])
            {
                var shape = (string)(await IntegrationDb.ScalarAsync(c,
                    $"""
                     SELECT CONCAT(t.name, '(', c.max_length, ')')
                     FROM sys.columns c
                     JOIN sys.types t ON t.user_type_id = c.user_type_id
                     WHERE c.object_id = OBJECT_ID(N'merch.{table}') AND c.name = N'SaleCode';
                     """))!;
                Assert.Equal("varchar(20)", shape);
            }
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    /// <summary>Renders the production migration's <c>Up()</c> through EF's real SQL Server generator. Rendering
    /// (rather than hand-writing the expected SQL) is the point: whatever the migration says is what runs, so a
    /// rename rewritten as DropColumn+AddColumn reaches the database exactly as it would in production.</summary>
    private static IReadOnlyList<string> RenderUpScript()
    {
        var migration = (Migration)new RenameProducerCodeToSaleCode();
        using var context = new ScratchContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        return [.. generator.Generate(migration.UpOperations).Select(command => command.CommandText)];
    }

    /// <summary>The two columns as they stand BEFORE this migration: <c>nvarchar(64)</c>, nullable. Only the
    /// columns the migration touches are modelled — everything else in these tables is irrelevant to a rename,
    /// and a scratch database keeps the suite's shared VCentralPay untouched.</summary>
    private static async Task CreatePreMigrationTablesAsync(SqlConnection c)
    {
        await IntegrationDb.ExecAsync(c, "EXEC('CREATE SCHEMA merch');");
        foreach (var table in (string[])["Users", "RegistrationAttempts"])
            await IntegrationDb.ExecAsync(c,
                $"""
                 CREATE TABLE merch.{table} (
                     Id uniqueidentifier NOT NULL PRIMARY KEY,
                     ProducerCode nvarchar(64) NULL
                 );
                 """);
    }

    private static async Task CreateScratchDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        // EXEC(): CREATE DATABASE may not be parameterised and may not share a batch with other statements.
        await IntegrationDb.ExecAsync(master, $"EXEC('CREATE DATABASE [{database}]');");
    }

    private static async Task DropScratchDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(master,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    private static Task<SqlConnection> OpenScratchAsync(string database) =>
        IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));

    /// <summary>A model-less context that exists only to hand out EF's SQL Server migration generator — it is
    /// never connected to anything and never queried.</summary>
    private sealed class ScratchContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlServer("Server=(local);Database=unused;Trusted_Connection=True;");
    }
}
