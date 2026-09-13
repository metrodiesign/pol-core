using System.Data;
using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;

namespace Integration.Tests;

/// <summary>SQL Server keeps the session isolation level after a transaction ends and SqlClient pooling does not
/// reset it, so a SERIALIZABLE transaction (first-login JIT) leaked into the next pooled user of that
/// connection and the READPAST outbox dispatchers failed. The runtime contexts reset the level before EF
/// returns the connection to the pool; a second connection reads what SQL Server reports for that session.</summary>
[Trait("Category", "Integration")]
[Trait("Capability", "IdentityAccess")]
public sealed class ReadCommittedResetInterceptorTests
{
    private const int ReadCommitted = 2;
    private const int Serializable = 4;

    [Fact]
    public async Task Serializable_transaction_returns_the_pooled_session_at_read_committed()
    {
        // Pooling on so the session survives the close and can be inspected from a second connection.
        var connectionString = new SqlConnectionStringBuilder(IntegrationDb.SaConnFor("master")) { Pooling = true }
            .ConnectionString;
        short spid;
        await using (var db = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(170)).Options,
            AllowAll.Instance, NoOpSecurityTelemetry.Instance))
        {
            await db.Database.OpenConnectionAsync();
            spid = await db.Database.SqlQueryRaw<short>("SELECT @@SPID AS Value").SingleAsync();

            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
            {
                Assert.Equal(Serializable, await SessionLevelAsync(db));
                await transaction.CommitAsync();
            }
            Assert.Equal(Serializable, await SessionLevelAsync(db)); // SQL Server keeps it until we reset

            await db.Database.CloseConnectionAsync(); // reset runs here, then the session goes back to the pool
        }

        await using var observer = await IntegrationDb.OpenAsync(connectionString);
        await using var command = observer.CreateCommand();
        command.CommandText = "SELECT CAST(transaction_isolation_level AS int) FROM sys.dm_exec_sessions WHERE session_id = @spid";
        command.Parameters.AddWithValue("@spid", spid);
        Assert.Equal(ReadCommitted, (int)(await command.ExecuteScalarAsync())!);
    }

    private static Task<int> SessionLevelAsync(DbContext db) =>
        db.Database
            .SqlQueryRaw<int>("SELECT CAST(transaction_isolation_level AS int) AS Value FROM sys.dm_exec_sessions WHERE session_id = @@SPID")
            .SingleAsync();

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();

        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
