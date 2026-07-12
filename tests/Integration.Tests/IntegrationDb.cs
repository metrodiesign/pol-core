using BuildingBlocks.Infrastructure.Vault;
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

    /// <summary>Same principal as <see cref="AppConn"/> but with pooling ENABLED and <c>Max Pool Size=1</c> — every
    /// <c>Open()</c> after the first hands back the SAME physical connection once it has been returned to the pool.
    /// ONLY for the pooled connection-reuse proof (T13/REQ-4.1); every other connection in this suite disables
    /// pooling by design (see class doc) so a fresh physical connection never inherits a stale SESSION_CONTEXT.</summary>
    public static string PooledAppConn =>
        $"Server={Server};Database={Db};User Id=pol_app;Password={Require("POL_APP_PASSWORD")};" +
        "Encrypt=True;TrustServerCertificate=True;Pooling=True;Max Pool Size=1;";

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

    /// <summary>REQ-3.4's exact scenario: BOTH keys explicitly stamped (empty MerchantId sentinel, UserId
    /// explicitly NULL) — as opposed to <see cref="OpenAsync(string,Guid?)"/> with no merchant, which stamps
    /// nothing at all (REQ-3.5's "no context whatsoever" case). Proves the predicate's own guard denies even when
    /// something manages to stamp the sentinel with no resolvable user — belt-and-suspenders under the app-layer
    /// throw guard (REQ-4.3), which this raw-connection probe deliberately bypasses to hit the SQL predicate directly.</summary>
    public static async Task<SqlConnection> OpenWithUnboundEmptySentinelAsync(string connString)
    {
        var connection = new SqlConnection(connString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "EXEC sys.sp_set_session_context @key=N'MerchantId', @value=@empty, @read_only=1;" +
            "EXEC sys.sp_set_session_context @key=N'UserId', @value=NULL, @read_only=1;";
        cmd.Parameters.AddWithValue("@empty", Guid.Empty);
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

    /// <summary>Convenience for the Scoped-admin matrix (T5/REQ-3.3/REQ-3.11): creates a fresh Scoped platform user
    /// (Tier=Scoped) and grants it <see cref="Admins.Domain.MerchantAccess"/> to each of
    /// <paramref name="assignedMerchantIds"/> — zero merchants is a valid, deliberate call shape (the REQ-3.11
    /// fail-closed case: a Scoped actor with no PMA rows at all must see nothing, not everything).</summary>
    public static async Task<SqlConnection> OpenAsNewScopedUserAsync(params Guid[] assignedMerchantIds)
    {
        var userId = Guid.NewGuid();
        await using (var bare = await OpenAsync(AdminConn))
        {
            await InsertPlatformUserAsync(bare, userId, "scoped-" + userId.ToString("N")[..8],
                userId.ToString("N")[..8] + "@example.com", tier: 0, status: 0);
            foreach (var merchantId in assignedMerchantIds)
                await InsertPlatformMerchantAccessAsync(bare, Guid.NewGuid(), userId, merchantId, Guid.NewGuid(), DateTime.UtcNow);
        }
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

    public static Task InsertPlatformMerchantAccessAsync(
        SqlConnection c, Guid id, Guid platformUserId, Guid merchantId, Guid assignedByAdminId, DateTime assignedAt) =>
        ExecAsync(c,
            """
            INSERT admin.PlatformMerchantAccess (Id, PlatformUserId, MerchantId, AssignedByAdminId, AssignedAt)
            VALUES (@id, @u, @m, @by, @at);
            """,
            ("@id", id), ("@u", platformUserId), ("@m", merchantId), ("@by", assignedByAdminId), ("@at", assignedAt));

    public static Task InsertCartAsync(SqlConnection c, Guid id, Guid merchantId, string status = "Open") =>
        ExecAsync(c,
            """
            INSERT shop.Carts (Id, MerchantId, Status, CreatedAt)
            VALUES (@id, @m, @status, SYSUTCDATETIME());
            """,
            ("@id", id), ("@m", merchantId), ("@status", status));

    /// <summary>CartItems carries no MerchantId of its own — it is parent-scoped through shop.Carts via
    /// <c>sec.fn_cartitem_predicate</c>, so its isolation rides on the parent cart's merchant, not a column here.</summary>
    public static Task InsertCartItemAsync(
        SqlConnection c, Guid id, Guid cartId, Guid productId, int quantity, decimal unitPriceAmount, string unitPriceCurrency) =>
        ExecAsync(c,
            """
            INSERT shop.CartItems (Id, CartId, ProductId, Quantity, UnitPriceAmount, UnitPriceCurrency)
            VALUES (@id, @cart, @product, @qty, @amt, @cur);
            """,
            ("@id", id), ("@cart", cartId), ("@product", productId), ("@qty", quantity),
            ("@amt", unitPriceAmount), ("@cur", unitPriceCurrency));

    public static Task InsertCheckoutSessionAsync(
        SqlConnection c, Guid id, Guid merchantId, Guid cartId, int status, decimal amount, string currency) =>
        ExecAsync(c,
            """
            INSERT shop.CheckoutSessions (Id, MerchantId, CartId, AmountAmount, AmountCurrency, Status, CreatedAt)
            VALUES (@id, @m, @cart, @amt, @cur, @status, SYSUTCDATETIME());
            """,
            ("@id", id), ("@m", merchantId), ("@cart", cartId), ("@amt", amount), ("@cur", currency), ("@status", status));

    public static Task InsertPaymentSessionAsync(
        SqlConnection c, Guid id, Guid merchantId, Guid orderId, decimal amount, string currency, string method, int psp, int status) =>
        ExecAsync(c,
            """
            INSERT txn.PaymentSessions (Id, MerchantId, OrderId, AmountAmount, AmountCurrency, Method, Psp, Status, CreatedAt, UpdatedAt)
            VALUES (@id, @m, @order, @amt, @cur, @method, @psp, @status, SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            ("@id", id), ("@m", merchantId), ("@order", orderId), ("@amt", amount), ("@cur", currency),
            ("@method", method), ("@psp", psp), ("@status", status));

    public static Task InsertIdempotencyRecordAsync(SqlConnection c, string key, Guid merchantId, string context) =>
        ExecAsync(c,
            """
            INSERT txn.IdempotencyRecords ([Key], MerchantId, Context, CreatedAt)
            VALUES (@key, @m, @ctx, SYSUTCDATETIME());
            """,
            ("@key", key), ("@m", merchantId), ("@ctx", context));

    public static Task InsertVaultSecretAsync(SqlConnection c, Guid merchantId, string name, string keyId, string hint) =>
        ExecAsync(c,
            """
            INSERT merch.VaultSecrets (MerchantId, Name, KeyId, EncryptedDek, EncryptedSecret, Hint, CreatedAt, UpdatedAt)
            VALUES (@m, @name, @keyId, @dek, @secret, @hint, SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            ("@m", merchantId), ("@name", name), ("@keyId", keyId),
            ("@dek", new byte[] { 1, 2, 3, 4 }), ("@secret", new byte[] { 5, 6, 7, 8 }), ("@hint", hint));

    /// <summary>Inserts a Seq=1 (genesis-chained) reveal-audit row using the SAME hash construction
    /// <see cref="VaultRevealAuditWriter"/> uses in production, so a probe row is indistinguishable from a real
    /// one. Returns the computed hash for callers that verify <c>sec.usp_vault_audit_head</c>'s result.</summary>
    public static async Task<byte[]> InsertVaultRevealAuditAsync(SqlConnection c, Guid merchantId, string secretName, DateTime revealedAt)
    {
        var hash = VaultRevealAudit.ComputeHash(VaultRevealAudit.Genesis, merchantId, secretName, 1, revealedAt);
        await ExecAsync(c,
            """
            INSERT merch.VaultRevealAudits (MerchantId, SecretName, Seq, PrevHash, Hash, RevealedAt)
            VALUES (@m, @name, 1, @prev, @hash, @at);
            """,
            ("@m", merchantId), ("@name", secretName), ("@prev", VaultRevealAudit.Genesis), ("@hash", hash), ("@at", revealedAt));
        return hash;
    }

    private static string? Get(string key) => Environment.GetEnvironmentVariable(key);

    private static string Require(string key) =>
        Get(key) ?? throw new InvalidOperationException(
            $"Integration tests need env var '{key}'. Run docker/bootstrap/01-principals.sql and export the principal passwords.");
}
