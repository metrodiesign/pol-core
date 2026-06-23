using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Connection helpers for the live SQL Server 2025 RLS suite. Credentials come from environment
/// variables ONLY (never committed): POL_SQL_SERVER, POL_DB, and POL_SA_PASSWORD / POL_APP_PASSWORD /
/// POL_ADMIN_PASSWORD / POL_WORKER_PASSWORD. Pooling is disabled so every open is a fresh physical
/// connection with no inherited SESSION_CONTEXT — the tests assert RLS, not the pool's reset behaviour.
/// </summary>
internal static class IntegrationDb
{
    public static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    public static readonly Guid TenantB = Guid.Parse("b0000000-0000-0000-0000-0000000000b1");

    private static string Server => Get("POL_SQL_SERVER") ?? "localhost,11433";
    private static string Db => Get("POL_DB") ?? "PaymentOrchestration";

    public static string AppConn => For("pol_app", "POL_APP_PASSWORD");
    public static string AdminConn => For("pol_admin", "POL_ADMIN_PASSWORD");
    public static string WorkerConn => For("pol_worker", "POL_WORKER_PASSWORD");
    public static string SaConn => For("sa", "POL_SA_PASSWORD");

    private static string For(string user, string pwEnv) =>
        $"Server={Server};Database={Db};User Id={user};Password={Require(pwEnv)};" +
        "Encrypt=True;TrustServerCertificate=True;Pooling=False";

    /// <summary>Opens a connection and (optionally) binds the tenant via read-only SESSION_CONTEXT.</summary>
    public static async Task<SqlConnection> OpenAsync(string connString, Guid? tenant = null)
    {
        var connection = new SqlConnection(connString);
        await connection.OpenAsync();
        if (tenant is not null)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "EXEC sys.sp_set_session_context @key=N'TenantId', @value=@t, @read_only=1;";
            cmd.Parameters.AddWithValue("@t", tenant.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        return connection;
    }

    public static async Task<int> ExecAsync(SqlConnection c, string sql, params (string, object)[] args)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(SqlConnection c, string sql, params (string, object)[] args)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync();
    }

    public static Task InsertProductAsync(SqlConnection c, Guid id, Guid tenantId) =>
        ExecAsync(c,
            """
            INSERT producer.Products (Id, TenantId, Name, PriceMinorUnits, PriceCurrency, IsActive, CreatedAtUtc)
            VALUES (@id, @t, N'probe', 100, N'THB', 1, SYSUTCDATETIME());
            """,
            ("@id", id), ("@t", tenantId));

    public static Task InsertPspConnectionAsync(SqlConnection c, Guid id, Guid tenantId) =>
        ExecAsync(c,
            """
            INSERT producer.PspConnections (Id, TenantId, Psp, EnabledMethods, SecretRefName, IsEnabled, CreatedAtUtc)
            VALUES (@id, @t, 0, N'card', N'secret', 1, SYSUTCDATETIME());
            """,
            ("@id", id), ("@t", tenantId));

    /// <summary>Inserts an order. SummaryToken/SummaryTokenExpiresAtUtc are omitted so the migration's
    /// per-row defaults apply. Status is the <c>OrderStatus</c> int (1 = Paid).</summary>
    public static Task InsertOrderAsync(SqlConnection c, Guid id, Guid tenantId, int status, long minorUnits, string currency) =>
        ExecAsync(c,
            """
            INSERT producer.Orders (Id, TenantId, AmountMinorUnits, AmountCurrency, Status, CreatedAtUtc)
            VALUES (@id, @t, @amt, @cur, @st, SYSUTCDATETIME());
            """,
            ("@id", id), ("@t", tenantId), ("@amt", minorUnits), ("@cur", currency), ("@st", status));

    /// <summary>Inserts an active Tenant master row. The PK <paramref name="id"/> IS the tenant identity the
    /// RLS predicate scopes on; <paramref name="code"/> is the unique idempotency key.</summary>
    public static Task InsertTenantAsync(SqlConnection c, Guid id, string code) =>
        ExecAsync(c,
            """
            INSERT producer.Tenants (Id, Code, DisplayName, LegalEntityId, Status, Country, Currency, EnabledChannels, CreatedAtUtc, Metadata)
            VALUES (@id, @code, N'probe', N'0105560000000', 0, N'TH', N'THB', N'card', SYSUTCDATETIME(), N'{}');
            """,
            ("@id", id), ("@code", code));

    private static string? Get(string key) => Environment.GetEnvironmentVariable(key);

    private static string Require(string key) =>
        Get(key) ?? throw new InvalidOperationException(
            $"Integration tests need env var '{key}'. Run docker/bootstrap/01-principals.sql and export the principal passwords.");
}
