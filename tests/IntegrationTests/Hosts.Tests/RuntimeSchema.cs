using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;

namespace Hosts.Tests;

/// <summary>SQLite EnsureCreated stops after the first runtime model finds any table. Create the Control Plane
/// model, then execute the Commerce model's provider-specific script so dual-context fixture tests share one
/// in-memory database without restoring duplicate SQL-schema owners.</summary>
internal static class RuntimeSchema
{
    public static void EnsureCreated(
        ControlPlaneDbContext controlPlane,
        CommerceDbContext commerce,
        SqliteConnection connection)
    {
        controlPlane.Database.EnsureCreated();
        using var command = connection.CreateCommand();
        command.CommandText = commerce.Database.GenerateCreateScript();
        command.ExecuteNonQuery();
        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'PaymentSessions';";
        if (Convert.ToInt32(verify.ExecuteScalar()) == 0)
            throw new InvalidOperationException("RuntimeSchema did not create Commerce PaymentSessions table.");
    }
}
