using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Tests;

/// <summary>
/// Backs a real <see cref="ProducerDbContext"/> with an in-memory Sqlite database over a kept-alive
/// connection (the schema lives only while the connection is open). Offline — no SQL Server, no
/// network — so these are unit tests, not [Trait Integration]. Disposing the harness closes the
/// connection and discards the database.
/// </summary>
internal sealed class ProducerDbContextTestHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    private ProducerDbContextTestHarness(SqliteConnection connection) => _connection = connection;

    public static ProducerDbContextTestHarness Create()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var harness = new ProducerDbContextTestHarness(connection);

        // Create the producer schema once; subsequent contexts share the same in-memory database
        // for as long as the connection stays open.
        using var ctx = harness.NewContext();
        ctx.Database.EnsureCreated();

        return harness;
    }

    /// <summary>
    /// A fresh DbContext over the shared connection — mirrors the per-scope context the host hands
    /// each Mediator handler, so a "replay" arriving on a new context still sees the first delivery.
    /// </summary>
    public ProducerDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ProducerDbContext>()
            .UseSqlite(_connection)
            .Options;

        // ModuleAssemblies with empty lists: BuildingBlocks owns its tables and discovers no modules.
        var modules = new ModuleAssemblies([], []);
        return new ProducerDbContext(options, modules);
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>Deterministic <see cref="IClock"/> so persisted timestamps are assertable.</summary>
internal sealed class FixedClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; } = utcNow;
}
