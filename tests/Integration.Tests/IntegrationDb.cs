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
    public static readonly Guid MerchantA = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    public static readonly Guid MerchantB = Guid.Parse("b0000000-0000-0000-0000-0000000000b1");

    private static string Server => Get("POL_SQL_SERVER") ?? "localhost,11433";
    private static string Db => Get("POL_DB") ?? "VCentralPay";

    public static string AppConn => For("pol_app", "POL_APP_PASSWORD");
    public static string AdminConn => For("pol_admin", "POL_ADMIN_PASSWORD");
    public static string WorkerConn => For("pol_worker", "POL_WORKER_PASSWORD");
    public static string SaConn => For("sa", "POL_SA_PASSWORD");

    private static string For(string user, string pwEnv) =>
        $"Server={Server};Database={Db};User Id={user};Password={Require(pwEnv)};" +
        "Encrypt=True;TrustServerCertificate=True;Pooling=False";

    /// <summary>Opens a connection and (optionally) binds the merchant via read-only SESSION_CONTEXT.</summary>
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

    /// <summary>Opens a connection bound as a specific platform user (T5): stamps the empty-MerchantId sentinel +
    /// UserId, mirroring what AdminActorContext/SessionContextConnectionInterceptor stamp for a real authenticated
    /// admin request. sec.fn_merchant_predicate only takes this branch (Super sees all / Scoped sees its assigned
    /// merchants) for a principal NOT in pol_rls_bypass with the sentinel + a resolvable UserId — pol_admin alone,
    /// with no SESSION_CONTEXT at all, no longer sees anything (T5 removed it from pol_rls_bypass).</summary>
    public static async Task<SqlConnection> OpenAsPlatformUserAsync(string connString, Guid platformUserId)
    {
        var connection = new SqlConnection(connString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "EXEC sys.sp_set_session_context @key=N'MerchantId', @value=@empty, @read_only=1;" +
            "EXEC sys.sp_set_session_context @key=N'UserId', @value=@u, @read_only=1;";
        cmd.Parameters.AddWithValue("@empty", Guid.Empty);
        cmd.Parameters.AddWithValue("@u", platformUserId);
        await cmd.ExecuteNonQueryAsync();
        return connection;
    }

    /// <summary>Convenience for provisioning tests (T5): creates a fresh Super platform user (via a throwaway bare
    /// admin connection — admin.PlatformUsers carries no RLS predicate, so that insert alone needs no binding),
    /// then returns a connection bound to it. merch.Merchants / txn.PspConnections DO carry the merchant predicate,
    /// so provisioning them now requires this bound Super identity rather than the old blanket pol_admin bypass.</summary>
    public static async Task<SqlConnection> OpenAsNewSuperUserAsync()
    {
        var userId = Guid.NewGuid();
        await using (var bare = await OpenAsync(AdminConn))
            await InsertPlatformUserAsync(bare, userId, "super-" + userId.ToString("N")[..8],
                userId.ToString("N")[..8] + "@example.com", tier: 1, status: 0);
        return await OpenAsPlatformUserAsync(AdminConn, userId);
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

    public static Task InsertProductAsync(SqlConnection c, Guid id, Guid merchantId) =>
        ExecAsync(c,
            """
            INSERT shop.Products (Id, MerchantId, Name, PriceAmount, PriceCurrency, IsActive, CreatedAt)
            VALUES (@id, @m, N'probe', 100, N'THB', 1, SYSUTCDATETIME());
            """,
            ("@id", id), ("@m", merchantId));

    public static Task InsertPspConnectionAsync(SqlConnection c, Guid id, Guid merchantId) =>
        ExecAsync(c,
            """
            INSERT txn.PspConnections (Id, MerchantId, Psp, EnabledMethods, SecretRefName, IsEnabled, CreatedAt)
            VALUES (@id, @m, 0, N'card', N'secret', 1, SYSUTCDATETIME());
            """,
            ("@id", id), ("@m", merchantId));

    /// <summary>Inserts an order. SummaryToken/SummaryTokenExpiresAt have no SQL-level default (OrderConfiguration
    /// just marks them required — the Order domain entity generates them in C#), so a raw probe insert must supply
    /// its own. Status is the <c>OrderStatus</c> int (1 = Paid).</summary>
    public static Task InsertOrderAsync(SqlConnection c, Guid id, Guid merchantId, int status, decimal amount, string currency) =>
        ExecAsync(c,
            """
            INSERT shop.Orders (Id, MerchantId, AmountAmount, AmountCurrency, Status, CreatedAt, SummaryToken, SummaryTokenExpiresAt)
            VALUES (@id, @m, @amt, @cur, @st, SYSUTCDATETIME(), @token, DATEADD(DAY, 30, SYSUTCDATETIME()));
            """,
            ("@id", id), ("@m", merchantId), ("@amt", amount), ("@cur", currency), ("@st", status),
            ("@token", Guid.NewGuid().ToString("N")));

    /// <summary>Inserts an active Merchant master row. The PK <paramref name="id"/> IS the merchant identity the
    /// RLS predicate scopes on; <paramref name="code"/> is the unique idempotency key.</summary>
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
            INSERT admin.PlatformUsers (Id, Subject, Email, Tier, Status, CreatedAt)
            VALUES (@id, @sub, @email, @tier, @status, SYSUTCDATETIME());
            """,
            ("@id", id), ("@sub", (object?)subject ?? DBNull.Value), ("@email", email), ("@tier", tier), ("@status", status));

    private static string? Get(string key) => Environment.GetEnvironmentVariable(key);

    private static string Require(string key) =>
        Get(key) ?? throw new InvalidOperationException(
            $"Integration tests need env var '{key}'. Run docker/bootstrap/01-principals.sql and export the principal passwords.");
}
