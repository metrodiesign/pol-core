using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Connection helpers for the live SQL Server 2025 suite. Credentials come from environment variables ONLY
/// (never committed): POL_SQL_SERVER, POL_DB, POL_SA_PASSWORD, POL_APP_PASSWORD. rls-to-query-filter task 8
/// (RlsTeardownAndOnePrincipal) collapsed pol_admin/pol_worker/pol_resolver/pol_vault_auditor into pol_app —
/// every test in this suite now runs on that one principal (plus <c>sa</c> for the vault-audit applock
/// tests, which are principal-agnostic). Pooling is disabled by default so every open is a fresh physical
/// connection — a leftover habit from the old SESSION_CONTEXT-based RLS suite that is harmless to keep (a
/// fresh connection per test is simply less surprising) now that nothing stamps SESSION_CONTEXT at all.
/// </summary>
internal static class IntegrationDb
{
    public static readonly Guid MerchantA = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    public static readonly Guid MerchantB = Guid.Parse("b0000000-0000-0000-0000-0000000000b1");

    private static string Server => Get("POL_SQL_SERVER") ?? "localhost,11433";
    private static string Db => Get("POL_DB") ?? "VCentralPay";

    public static string AppConn => For("pol_app", "POL_APP_PASSWORD");
    public static string SaConn => For("sa", "POL_SA_PASSWORD");

    /// <summary>The same pol_app connection pointed at another catalogue on the same instance. The simulated
    /// upstream databases (<c>hippodb</c> / <c>mammothdb</c>, created by docker/bootstrap/02-external-sim.sql)
    /// sit beside the app database, and pol_app holds nothing there but EXECUTE on the two search procedures —
    /// so every call through this connection also proves the GRANT.</summary>
    public static string ForCatalog(string catalog) => For("pol_app", "POL_APP_PASSWORD", catalog);

    private static string For(string user, string pwEnv, string? catalog = null) =>
        $"Server={Server};Database={catalog ?? Db};User Id={user};Password={Require(pwEnv)};" +
        "Encrypt=True;TrustServerCertificate=True;Pooling=False";

    /// <summary>Opens a connection and (optionally) binds the merchant via read-only SESSION_CONTEXT — kept
    /// for callers that still want a per-merchant marker on the connection for readability, though no
    /// predicate reads it anymore (task 8 tore down RLS; isolation is app-layer now).</summary>
    public static async Task<SqlConnection> OpenAsync(string connString, Guid? merchant = null)
    {
        var connection = new SqlConnection(connString);
        await connection.OpenAsync();
        if (merchant is not null)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "EXEC sys.sp_set_session_context @key=N'MerchantId', @value=@m, @read_only=1;";
            cmd.Parameters.AddWithValue("@m", merchant.Value);
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

    /// <summary>Inserts an active Merchant master row. <paramref name="code"/> is the unique idempotency key.</summary>
    public static Task InsertMerchantAsync(SqlConnection c, Guid id, string code) =>
        ExecAsync(c,
            """
            INSERT merch.Merchants (Id, Code, DisplayName, LegalEntityId, Status, Country, Currency, EnabledChannels, CreatedAt, Metadata)
            VALUES (@id, @code, N'probe', N'0105560000000', 0, N'TH', N'THB', N'card', SYSUTCDATETIME(), N'{}');
            """,
            ("@id", id), ("@code", code));

    /// <summary>Inserts a platform user (control-plane admin account). A null <paramref name="subject"/> models an
    /// invited Scoped account before its first login binds it (the filtered unique index exempts NULL subjects).
    /// Tier: Scoped=0, Super=1. Status: Active=0, Suspended=1.</summary>
    public static Task InsertPlatformUserAsync(SqlConnection c, Guid id, string? subject, string email, int tier, int status) =>
        ExecAsync(c,
            """
            INSERT admin.Users (Id, Subject, Email, Tier, Status, CreatedAt)
            VALUES (@id, @sub, @email, @tier, @status, SYSUTCDATETIME());
            """,
            ("@id", id), ("@sub", (object?)subject ?? DBNull.Value), ("@email", email), ("@tier", tier), ("@status", status));

    private static string? Get(string key) => Environment.GetEnvironmentVariable(key);

    private static string Require(string key) =>
        Get(key) ?? throw new InvalidOperationException(
            $"Integration tests need env var '{key}'. Run docker/bootstrap/01-principals.sql and export the principal passwords.");
}
