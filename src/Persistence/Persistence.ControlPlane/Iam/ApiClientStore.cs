using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Contracts;
using Governance.Domain;
using Iam.Application.ApiClients;
using Iam.Domain.ApiClients;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane.Governance;

namespace Persistence.ControlPlane.Iam;

internal sealed class ApiClientStore(
    ControlPlaneDbContext db,
    IClock clock,
    VaultKeyring keyring,
    IDataProtectionProvider protection,
    IUnitOfWork unitOfWork,
    ControlPlaneOperationExecutor operations) : IApiClientStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = protection.CreateProtector("pol-core/api-client-reveal/v1");

    public async Task<PagedResult<ApiClientView>> ListAsync(ApiClientAccess access, int page, int limit,
        string? search, Guid? merchantId, string? status, CancellationToken cancellationToken)
    {
        var source = Scope(db.ApiClients.AsNoTracking(), access);
        if (merchantId is { } selected) source = source.Where(x => x.MerchantId == selected);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{SfsLike.Escape(search.Trim())}%";
            source = source.Where(x => EF.Functions.Like(x.Name, pattern, "\\")
                || EF.Functions.Like(x.PublicClientId, pattern, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var parsed = status.Equals("active", StringComparison.OrdinalIgnoreCase)
                ? ApiClientStatus.Active
                : status.Equals("revoked", StringComparison.OrdinalIgnoreCase)
                    ? ApiClientStatus.Revoked
                    : throw new InvalidRequestException("API client status is invalid.", "invalid_filter");
            source = source.Where(x => x.Status == parsed);
        }
        var total = await source.LongCountAsync(cancellationToken);
        var rows = await source.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Skip((page - 1) * limit).Take(limit).ToListAsync(cancellationToken);
        return new(rows.Select(View).ToArray(), page, limit, total);
    }

    public async Task<ApiClientView?> GetAsync(
        Guid id, ApiClientAccess access, CancellationToken cancellationToken)
    {
        var row = await Scope(db.ApiClients.AsNoTracking(), access)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return row is null ? null : View(row);
    }

    public async Task<ApiClientCreated> CreateAsync(
        ApiClientCreate input, ApiClientAccess access, CancellationToken cancellationToken)
    {
        EnsureAccess(access, input.MerchantId);
        ValidateScopes(input.Scopes);
        ValidateIpPolicy(input.IpPolicy);
        var executed = await operations.ExecuteAsync(
            input.ActorId, input.MerchantId, "api-client.create", input.IdempotencyKey,
            new { input.Name, input.MerchantId, input.OriginatorId, input.Scopes, input.IpPolicy }, 201,
            async ct =>
            {
                var publicId = $"cli_live_{Token(12)}";
                var secret = $"pol_{Token(32)}";
                var secretBytes = Encoding.UTF8.GetBytes(secret);
                try
                {
                    var client = ApiClient.Create(publicId, input.Name, input.MerchantId, input.OriginatorId,
                        input.Scopes, input.IpPolicy, HashSecret(secretBytes), $"••••{secret[^4..]}", clock.UtcNow);
                    db.ApiClients.Add(client);
                    var (ticket, token) = ReadyTicket(client.Id, secret);
                    db.OneTimeSecretTickets.Add(ticket);
                    await Task.CompletedTask;
                    return new ApiClientCreated(View(client),
                        new OneTimeSecretTicketView(token, ticket.ExpiresAt), Replayed: false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secretBytes);
                }
            }, cancellationToken);
        return executed.Value with { Replayed = executed.Replayed };
    }

    public async Task<ApiClientMutation?> UpdateAsync(
        ApiClientUpdate input, ApiClientAccess access, CancellationToken cancellationToken)
    {
        ValidateScopes(input.Scopes);
        ValidateIpPolicy(input.IpPolicy);
        var merchantId = await MerchantIdAsync(input.Id, access, cancellationToken);
        if (merchantId is null) return null;
        var executed = await operations.ExecuteAsync(
            input.ActorId, merchantId.Value, "api-client.update", input.IdempotencyKey,
            new { input.Id, input.Name, input.Scopes, input.IpPolicy, input.ExpectedVersion }, 200,
            async ct =>
            {
                var row = await Scope(db.ApiClients, access).SingleAsync(x => x.Id == input.Id, ct);
                EnsureVersion(row.Version, input.ExpectedVersion);
                try { row.Update(input.Name, input.Scopes, input.IpPolicy, clock.UtcNow); }
                catch (InvalidOperationException ex) { throw new ConflictException(ex.Message, "state_conflict"); }
                return new ApiClientMutation(View(row), Replayed: false);
            }, cancellationToken);
        return executed.Value with { Replayed = executed.Replayed };
    }

    public async Task<ApiClientMutation?> RevokeAsync(Guid id, long expectedVersion, Guid actorId,
        string idempotencyKey, ApiClientAccess access, CancellationToken cancellationToken)
    {
        var merchantId = await MerchantIdAsync(id, access, cancellationToken);
        if (merchantId is null) return null;
        var executed = await operations.ExecuteAsync(
            actorId, merchantId.Value, "api-client.revoke", idempotencyKey,
            new { id, expectedVersion }, 200,
            async ct =>
            {
                var row = await Scope(db.ApiClients, access).SingleAsync(x => x.Id == id, ct);
                EnsureVersion(row.Version, expectedVersion);
                row.Revoke(clock.UtcNow);
                return new ApiClientMutation(View(row), Replayed: false);
            }, cancellationToken);
        return executed.Value with { Replayed = executed.Replayed };
    }

    public async Task<ApiClientRotationRequested?> RequestRotationAsync(
        Guid id, long expectedVersion, Guid actorId, string idempotencyKey, string correlationId,
        ApiClientAccess access, CancellationToken cancellationToken)
    {
        var merchantId = await MerchantIdAsync(id, access, cancellationToken);
        if (merchantId is null) return null;
        var executed = await operations.ExecuteAsync(
            actorId, merchantId.Value, "api-client.secret.rotate", idempotencyKey,
            new { id, expectedVersion }, 202,
            async ct =>
            {
                var row = await Scope(db.ApiClients, access).SingleAsync(x => x.Id == id, ct);
                EnsureVersion(row.Version, expectedVersion);
                var approvalId = Guid.CreateVersion7();
                var token = Token(32);
                var ticket = OneTimeSecretTicket.CreatePending(row.Id, approvalId,
                    SHA256.HashData(Encoding.UTF8.GetBytes(token)), clock.UtcNow);
                try { row.RequestRotation(approvalId, ticket.Id, clock.UtcNow); }
                catch (InvalidOperationException ex) { throw new ConflictException(ex.Message, "state_conflict"); }
                db.OneTimeSecretTickets.Add(ticket);
                var requested = new ApprovalRequested(
                    Guid.CreateVersion7(), approvalId, "merchant", row.MerchantId,
                    "api-client.secret.rotate", "apikey.manage", actorId,
                    "api-client-secret", row.Id.ToString("D"), $"v{row.Version}",
                    correlationId, clock.UtcNow);
                db.GovernanceOutboxMessages.Add(GovernanceOutboxMessage.Create(
                    requested.EventId, GovernanceScopeKind.Merchant, row.MerchantId,
                    ApprovalRequested.EventType, ApprovalRequested.SchemaVersion,
                    JsonSerializer.Serialize(requested, Json), requested.OccurredAt));
                await Task.CompletedTask;
                return new ApiClientRotationRequested(
                    approvalId, new OneTimeSecretTicketView(token, ticket.ExpiresAt),
                    "pending", row.Version, Replayed: false);
            }, cancellationToken);
        return executed.Value with { Replayed = executed.Replayed };
    }

    public async Task<SecretRevealResult> RevealAsync(string ticket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 200)
            return new SecretRevealResult(SecretRevealState.Unknown, null);
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ticket));
                var row = await db.OneTimeSecretTickets.SingleOrDefaultAsync(x => x.TicketHash == hash, ct);
                if (row is null) return new SecretRevealResult(SecretRevealState.Unknown, null);
                if (row.Status == SecretTicketStatus.Pending)
                    return row.ExpiresAt <= clock.UtcNow
                        ? new SecretRevealResult(SecretRevealState.Expired, null)
                        : new SecretRevealResult(SecretRevealState.Pending, null);
                if (row.Status == SecretTicketStatus.Consumed)
                    return new SecretRevealResult(SecretRevealState.Consumed, null);
                if (row.Status == SecretTicketStatus.Rejected)
                    return new SecretRevealResult(SecretRevealState.Rejected, null);
                if (row.ExpiresAt <= clock.UtcNow)
                    return new SecretRevealResult(SecretRevealState.Expired, null);
                var client = await db.ApiClients.AsNoTracking().SingleAsync(x => x.Id == row.ApiClientId, ct);
                var secret = _protector.Unprotect(row.ProtectedSecret
                    ?? throw new InvalidOperationException("Ready secret ticket has no protected secret."));
                row.Consume(clock.UtcNow);
                await unitOfWork.SaveChangesAsync(ct);
                return new SecretRevealResult(SecretRevealState.Ready,
                    new SecretReveal(client.PublicClientId, secret));
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SecretRevealResult(SecretRevealState.Consumed, null);
        }
    }

    public async Task<bool> VerifyAsync(
        string clientId, string secret, string? remoteAddress, CancellationToken cancellationToken)
    {
        var row = await db.ApiClients.SingleOrDefaultAsync(
            x => x.PublicClientId == clientId && x.Status == ApiClientStatus.Active, cancellationToken);
        if (row is null || !IpAllowed(row.IpPolicy, remoteAddress)) return false;
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(row.SecretHash, HashSecret(bytes))) return false;
            row.Use(clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private (OneTimeSecretTicket Ticket, string Token) ReadyTicket(Guid clientId, string secret)
    {
        var token = Token(32);
        return (OneTimeSecretTicket.CreateReady(clientId,
            SHA256.HashData(Encoding.UTF8.GetBytes(token)), _protector.Protect(secret), clock.UtcNow), token);
    }

    private byte[] HashSecret(byte[] secret)
    {
        var (_, key) = keyring.Active;
        return HMACSHA256.HashData(key, secret);
    }

    private async Task<Guid?> MerchantIdAsync(
        Guid id, ApiClientAccess access, CancellationToken cancellationToken) =>
        await Scope(db.ApiClients.AsNoTracking(), access).Where(x => x.Id == id)
            .Select(x => (Guid?)x.MerchantId).SingleOrDefaultAsync(cancellationToken);

    private static string Token(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IQueryable<ApiClient> Scope(IQueryable<ApiClient> source, ApiClientAccess access) =>
        access.IsUnrestricted ? source : source.Where(x => access.MerchantIds.Contains(x.MerchantId));

    private static ApiClientView View(ApiClient x) => new(x.Id, x.PublicClientId, x.Name, x.MerchantId,
        x.OriginatorId, x.Scopes(), x.IpPolicy, x.SecretHint, x.Status.ToString().ToLowerInvariant(),
        x.PendingRotationApprovalId.HasValue, x.LastUsedAt, x.CreatedAt, x.UpdatedAt, x.Version);

    private static void EnsureAccess(ApiClientAccess access, Guid merchantId)
    {
        if (!access.Allows(merchantId)) throw new AccessDeniedException("Merchant is outside current scope.");
    }

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected) throw new ConcurrencyConflictException("API client changed.");
    }

    private static void ValidateScopes(IReadOnlyCollection<string> scopes)
    {
        string[] allowed = ["payments:create", "payments:read", "refunds:create", "refunds:read",
            "webhooks:read", "settlements:read"];
        if (scopes.Count == 0 || scopes.Any(x => !allowed.Contains(x, StringComparer.Ordinal))
            || scopes.Count != scopes.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidRequestException("API client scopes are invalid.", "invalid_scope");
    }

    private static void ValidateIpPolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy)) return;
        var values = policy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0 || values.Length > 64 || values.Any(x => !IPNetwork.TryParse(x, out _)))
            throw new InvalidRequestException("API client IP policy is invalid.", "validation_failed");
    }

    private static bool IpAllowed(string? policy, string? remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(policy)) return true;
        if (!IPAddress.TryParse(remoteAddress, out var address)) return false;
        return policy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => IPNetwork.TryParse(x, out var network) && network.Contains(address));
    }
}

internal sealed class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<ApiClient> builder)
    {
        builder.ToTable("ApiClients", SchemaNames.Iam); builder.HasKey(x => x.Id);
        builder.Property(x => x.PublicClientId).HasMaxLength(80).IsRequired(); builder.HasIndex(x => x.PublicClientId).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired(); builder.Property(x => x.ScopesCsv).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.IpPolicy).HasMaxLength(2000); builder.Property(x => x.SecretHash).HasMaxLength(32).IsRequired();
        builder.Property(x => x.SecretHint).HasMaxLength(32).IsRequired(); builder.Property(x => x.Status).HasConversion<int>();
        builder.HasIndex(x => new { x.MerchantId, x.Status }); builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasIndex(x => x.PendingRotationApprovalId).IsUnique().HasFilter("[PendingRotationApprovalId] IS NOT NULL");
    }
}

internal sealed class OneTimeSecretTicketConfiguration : IEntityTypeConfiguration<OneTimeSecretTicket>
{
    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<OneTimeSecretTicket> builder)
    {
        builder.ToTable("OneTimeSecretTickets", SchemaNames.Iam); builder.HasKey(x => x.Id);
        builder.Property(x => x.TicketHash).HasMaxLength(32).IsRequired(); builder.HasIndex(x => x.TicketHash).IsUnique();
        builder.Property(x => x.ProtectedSecret).HasMaxLength(4096); builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Version).IsConcurrencyToken(); builder.HasIndex(x => x.ExpiresAt);
        builder.HasIndex(x => x.ApprovalId).IsUnique().HasFilter("[ApprovalId] IS NOT NULL");
    }
}
