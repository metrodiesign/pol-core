using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using SharedKernel;

namespace Architecture.Tests;

/// <summary>
/// SQLite's EnsureCreated checks whether any table exists and therefore cannot be called once per runtime
/// context on the same connection: the second context would silently skip its tables. Create the Control
/// Plane normally, then execute the Commerce context's provider-specific create script over the same
/// connection so both runtime ownership models are present without asking SQLite to collapse SQL schemas.
/// </summary>
internal static class RuntimeSchema
{
    public static void EnsureCreated(SqliteConnection connection)
    {
        using var controlPlane = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(connection).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance, FakeActorContext.Unbound);
        controlPlane.Database.EnsureCreated();

        using var commerce = new CommerceDbContext(
            new DbContextOptionsBuilder<CommerceDbContext>().UseSqlite(connection).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        using var command = connection.CreateCommand();
        command.CommandText = commerce.Database.GenerateCreateScript();
        command.ExecuteNonQuery();
    }
}
