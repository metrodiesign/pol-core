using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Connection helpers for the live SQL Server 2025 suite. Credentials come from environment variables ONLY
/// (never committed): POL_SQL_SERVER, POL_DB, POL_SA_PASSWORD, POL_APP_PASSWORD, POL_HIPPO_SQL_SERVER,
/// POL_MAMMOTH_SQL_SERVER (external-sim-separate-containers — the last two route <see cref="SimServer"/>
/// to hippodb/mammothdb's own instances), POL_HIPPO_APP_PASSWORD, POL_MAMMOTH_APP_PASSWORD
/// (sim-db-separate-logins — each sim instance has its own principal with its own password).
/// rls-to-query-filter task 8
/// (RlsTeardownAndOnePrincipal) collapsed pol_admin/pol_worker/pol_resolver/pol_vault_auditor into pol_app —
/// every test that touches <see cref="AppConn"/> (the VCentralPay side of this suite) now runs on that one
/// principal (plus <c>sa</c> for the vault-audit applock tests, which are principal-agnostic); the sim side
/// goes through <see cref="ForCatalog"/> and its own per-instance principals instead. Pooling is disabled by default so every open is a fresh physical
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

    /// <summary>An <c>sa</c> connection to another database on the SAME instance as <see cref="SaConn"/> — for
    /// the tests that create a throwaway database rather than touching the shared VCentralPay
    /// (products-external-source-of-truth task 1). Distinct from <see cref="SaForCatalog"/>, which re-points the
    /// server as well because the simulated catalogues live on their own instances.</summary>
    public static string SaConnFor(string database) =>
        $"Server={Server};Database={database};User Id=sa;Password={Require("POL_SA_PASSWORD")};"
        + "Encrypt=True;TrustServerCertificate=True;Pooling=False";

    /// <summary>A connection to a simulated upstream catalogue — <c>hippodb</c>
    /// (docker/bootstrap/02-hippo-sim.sql) or <c>mammothdb</c> (docker/bootstrap/03-mammoth-sim.sql).
    /// external-sim-separate-containers moved each onto its OWN SQL Server instance (no longer beside the
    /// app database), so this routes through <see cref="SimServer"/> instead of <see cref="Server"/>;
    /// sim-db-separate-logins then gave each instance its OWN principal and password (hippo_app /
    /// mammoth_app), so the credential is picked per catalogue too — never pol_app, which belongs to
    /// VCentralPay alone. Each principal holds EXECUTE on its one search procedure (plus SELECT on
    /// dbo.Documents, so a developer's own credential can browse the seed), so every call through this
    /// connection also proves the EXECUTE GRANT. An unknown catalogue throws
    /// rather than falling back to a credential that would not work anyway.</summary>
    public static string ForCatalog(string catalog) => catalog switch
    {
        "hippodb" => For("hippo_app", "POL_HIPPO_APP_PASSWORD", catalog),
        "mammothdb" => For("mammoth_app", "POL_MAMMOTH_APP_PASSWORD", catalog),
        _ => throw new ArgumentOutOfRangeException(nameof(catalog), catalog, "Unknown simulated catalogue.")
    };

    /// <summary>The <c>sa</c> connection to a simulated upstream catalogue — used by
    /// <see cref="SimCrossInstanceConsistencyTests"/>, which reads <c>dbo.Documents</c> directly rather
    /// than through the search procedure.</summary>
    public static string SaForCatalog(string catalog) => For("sa", "POL_SA_PASSWORD", catalog);

    /// <summary>Routes a simulated catalogue name to its OWN SQL Server instance (external-sim-separate-
    /// containers REQ-3.2) — hippodb and mammothdb no longer share an instance with each other or with the
    /// app database, so there is no single <see cref="Server"/> to re-point Database= against.</summary>
    private static string SimServer(string catalog) => catalog switch
    {
        "hippodb" => Get("POL_HIPPO_SQL_SERVER") ?? "localhost,11434",
        "mammothdb" => Get("POL_MAMMOTH_SQL_SERVER") ?? "localhost,11435",
        _ => throw new ArgumentOutOfRangeException(nameof(catalog), catalog, "Unknown simulated catalogue.")
    };

    private static string For(string user, string pwEnv, string? catalog = null) =>
        $"Server={(catalog is null ? Server : SimServer(catalog))};Database={catalog ?? Db};User Id={user};Password={Require(pwEnv)};" +
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
            INSERT merch.Merchants (Id, Code, Name, Note, Status, Country, Currency, EnabledChannels, CreatedAt, Metadata)
            VALUES (@id, @code, N'probe', NULL, 0, N'TH', N'THB', N'card', SYSUTCDATETIME(), N'{}');
            """,
            ("@id", id), ("@code", code));

    /// <summary>Inserts a platform user (control-plane admin account). A null <paramref name="subject"/> models an
    /// invited Scoped account before its first login binds it (the filtered unique index exempts NULL subjects).
    /// Tier: Scoped=0, Super=1. Status: Active=0, Suspended=1.</summary>
    public static Task InsertPlatformUserAsync(SqlConnection c, Guid id, string? subject, string email, int tier, int status) =>
        ExecAsync(c,
            """
            INSERT admin.Users (Id, Subject, Email, Tier, Status, AuthorizationVersion, CreatedAt)
            VALUES (@id, @sub, @email, @tier, @status, 0, SYSUTCDATETIME());
            """,
            ("@id", id), ("@sub", (object?)subject ?? DBNull.Value), ("@email", email), ("@tier", tier), ("@status", status));

    private static string? Get(string key) => Environment.GetEnvironmentVariable(key);

    internal static string Require(string key) =>
        Get(key) ?? throw new InvalidOperationException(
            $"Integration tests need env var '{key}'. Run docker/bootstrap/01-principals.sql (pol_app), "
            + "02-hippo-sim.sql (hippo_app) and 03-mammoth-sim.sql (mammoth_app), then export the principal passwords.");
}
