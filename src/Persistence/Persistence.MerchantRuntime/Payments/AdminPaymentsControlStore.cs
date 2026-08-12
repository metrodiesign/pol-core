using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;
using Payments.Domain.Psp;
using Payments.Domain.Routing;

namespace Persistence.MerchantRuntime.Payments;

internal sealed class AdminPaymentsControlStore(
    MerchantRuntimeDbContext db,
    IClock clock,
    IUnitOfWork unitOfWork,
    IVaultSecretStore vault,
    IPspSecretEnvelopeFactory envelopeFactory,
    IPspAdapterFactory adapterFactory) : IAdminPaymentsControlStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PagedResult<PspConnectionView>> ListConnectionsAsync(
        PspConnectionQuery query, CancellationToken cancellationToken)
    {
        if (query.MerchantId is { } selected)
            EnsureAccess(query.Access, selected);
        var source = db.PspConnections.IgnoreQueryFilters().AsNoTracking();
        if (!query.Access.IsUnrestricted)
            source = source.Where(x => query.Access.MerchantIds.Contains(x.MerchantId));
        if (query.MerchantId is { } merchantId)
            source = source.Where(x => x.MerchantId == merchantId);
        if (!string.IsNullOrWhiteSpace(query.Psp))
        {
            var psp = ParsePsp(query.Psp);
            source = source.Where(x => x.Psp == psp);
        }
        if (!string.IsNullOrWhiteSpace(query.Health))
        {
            var health = ParseHealth(query.Health);
            source = source.Where(x => x.Health == health);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(x => x.Id.ToString().Contains(search));
        }

        var total = await PlatformReadGuard.ReadAsync(ct => source.LongCountAsync(ct), cancellationToken);
        var rows = await PlatformReadGuard.ReadAsync(ct => source.OrderBy(x => x.MerchantId)
            .ThenBy(x => x.Psp).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct), cancellationToken);
        var items = new List<PspConnectionView>(rows.Count);
        foreach (var row in rows)
            items.Add(await ProjectConnectionAsync(row, cancellationToken));
        return new PagedResult<PspConnectionView>(items, query.Page, query.Limit, total);
    }

    public async Task<PspConnectionView?> GetConnectionAsync(
        Guid connectionId, Guid? merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => db.PspConnections.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == connectionId && (merchantId == null || x.MerchantId == merchantId), ct),
            cancellationToken);
        return row is null || !access.Allows(row.MerchantId)
            ? null
            : await ProjectConnectionAsync(row, cancellationToken);
    }

    public Task<PspConnectionMutationResult> CreateConnectionAsync(
        CreatePspConnectionIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            await EnsureMerchantExistsAsync(intent.MerchantId, ct);
            var psp = ParsePsp(intent.Psp);
            var methods = ValidateMethods(psp, intent.EnabledMethods);
            ValidateConfig(intent.Config);
            var envelope = envelopeFactory.Build(new PspSecretInput(psp, intent.Secrets, intent.PspMerchantId));
            var intentHash = Hash(new
            {
                intent.MerchantId,
                psp = psp.ToCode(),
                methods,
                config = intent.Config?.GetRawText(),
                intent.PspMerchantId,
                secretFingerprint = SecretIntentFingerprint(envelope.EnvelopeJson),
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "psp.create", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return new PspConnectionMutationResult(await ReplayConnectionAsync(prior, ct), true);

            var connection = Connection.Create(intent.MerchantId, psp, string.Join(',', methods),
                $"psp-connection-{Guid.CreateVersion7():N}", clock.UtcNow,
                ConnectionMetadata(intent.PspMerchantId, intent.Config, envelope.Hints));
            var secretName = $"psp-connection-{connection.Id:N}";
            var candidate = await vault.StageVersionAsync(intent.MerchantId, secretName,
                envelope.EnvelopeJson, JsonSerializer.Serialize(envelope.Hints, Json), null, ct);
            await vault.ActivateVersionAsync(intent.MerchantId, candidate, ct);
            connection.SetInitialSecretVersion(candidate);
            db.PspConnections.Add(connection);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.create", intent.IdempotencyKey, intentHash);
            var view = await ProjectConnectionAsync(connection, ct);
            operation.Succeed(201, JsonSerializer.Serialize(view, Json), connection.Id.ToString("D"));
            await unitOfWork.SaveChangesAsync(ct);
            return new PspConnectionMutationResult(view, false);
        }, cancellationToken);

    public Task<PspConnectionMutationResult> UpdateConnectionAsync(
        UpdatePspConnectionIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            var connection = await LoadConnectionAsync(intent.ConnectionId, intent.MerchantId, ct);
            EnsureVersion(connection.Version, intent.ExpectedVersion);
            var methods = ValidateMethods(connection.Psp, intent.EnabledMethods);
            ValidateConfig(intent.Config);
            var intentHash = Hash(new
            {
                intent.ConnectionId,
                intent.MerchantId,
                methods,
                config = intent.Config?.GetRawText(),
                intent.IsEnabled,
                intent.ExpectedVersion,
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "psp.update", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return new PspConnectionMutationResult(await ReplayConnectionAsync(prior, ct), true);

            var metadata = ReadMetadata(connection.Metadata);
            connection.Update(string.Join(',', methods),
                ConnectionMetadata(metadata.PspMerchantId, intent.Config, metadata.Hints), intent.IsEnabled);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.update", intent.IdempotencyKey, intentHash);
            var view = await ProjectConnectionAsync(connection, ct);
            operation.Succeed(200, JsonSerializer.Serialize(view, Json), connection.Id.ToString("D"));
            await unitOfWork.SaveChangesAsync(ct);
            return new PspConnectionMutationResult(view, false);
        }, cancellationToken);

    public async Task<PspConnectionMutationResult> TestConnectionAsync(
        TestPspConnectionIntent intent, CancellationToken cancellationToken)
    {
        EnsureAccess(intent.Access, intent.MerchantId);
        var intentHash = Hash(new { intent.ConnectionId, intent.MerchantId, intent.ExpectedVersion });
        var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
            "psp.test", intent.IdempotencyKey, intentHash, cancellationToken);
        if (prior is not null)
        {
            var replay = await ReplayConnectionAsync(prior, cancellationToken);
            if (prior.HttpStatus == 502)
                throw new PspConnectionTestFailedException(replay);
            return new PspConnectionMutationResult(replay, true);
        }

        var snapshot = await PlatformReadGuard.ReadAsync(ct => db.PspConnections.IgnoreQueryFilters()
            .AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == intent.ConnectionId && x.MerchantId == intent.MerchantId, ct), cancellationToken)
            ?? throw new NotFoundException("PSP connection was not found.");
        EnsureVersion(snapshot.Version, intent.ExpectedVersion);

        var succeeded = false;
        try
        {
            var secret = snapshot.ActiveSecretVersionId is { } versionId
                ? await vault.ReadVersionForServerAsync(intent.MerchantId, versionId, cancellationToken)
                : await vault.RevealAsync(intent.MerchantId, snapshot.SecretRefName, cancellationToken);
            await adapterFactory.For(snapshot.Psp).TestConnectionAsync(secret, cancellationToken);
            succeeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            succeeded = false;
        }

        var current = await LoadConnectionAsync(intent.ConnectionId, intent.MerchantId, cancellationToken);
        EnsureVersion(current.Version, intent.ExpectedVersion);
        current.RecordTest(succeeded, succeeded ? "authenticated" : "probe_failed", clock.UtcNow);
        var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
            "psp.test", intent.IdempotencyKey, intentHash);
        var view = await ProjectConnectionAsync(current, cancellationToken);
        operation.Succeed(succeeded ? 200 : 502, JsonSerializer.Serialize(view, Json), current.Id.ToString("D"));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (!succeeded)
            throw new PspConnectionTestFailedException(view);
        return new PspConnectionMutationResult(view, false);
    }

    public Task<PspCredentialChangeResult> RequestCredentialChangeAsync(
        RequestPspCredentialChangeIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            var connection = await LoadConnectionAsync(intent.ConnectionId, intent.MerchantId, ct);
            EnsureVersion(connection.Version, intent.ExpectedVersion);
            var envelope = envelopeFactory.Build(new PspSecretInput(connection.Psp, intent.Secrets, intent.PspMerchantId));
            var intentHash = Hash(new
            {
                intent.ConnectionId,
                intent.MerchantId,
                intent.PspMerchantId,
                intent.ExpectedVersion,
                secretFingerprint = SecretIntentFingerprint(envelope.EnvelopeJson),
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "psp.credential-change", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return Replay<PspCredentialChangeResult>(prior);

            var secretName = $"psp-connection-{connection.Id:N}";
            var candidate = await vault.StageVersionAsync(intent.MerchantId, secretName,
                envelope.EnvelopeJson, JsonSerializer.Serialize(envelope.Hints, Json),
                clock.UtcNow.AddHours(24), ct);
            var approvalId = Guid.CreateVersion7();
            connection.StageSecretVersion(candidate, approvalId);
            var targetVersion = $"v{connection.Version}";
            var result = new PspCredentialChangeResult(approvalId, candidate, "pending", false);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.credential-change", intent.IdempotencyKey, intentHash);
            operation.Succeed(202, JsonSerializer.Serialize(result, Json), approvalId.ToString("D"));
            EnqueueApproval(new ApprovalRequested(
                Guid.CreateVersion7(), approvalId, "merchant", intent.MerchantId,
                "psp.credential.change", "settings.manage", intent.Access.ActorId,
                "psp-credential-version", connection.Id.ToString("D"), targetVersion,
                intent.CorrelationId, clock.UtcNow));
            await unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    public async Task<PagedResult<RoutingRulesetView>> ListRulesetsAsync(
        RoutingRulesetQuery query, CancellationToken cancellationToken)
    {
        if (query.MerchantId is { } selected)
            EnsureAccess(query.Access, selected);
        var source = db.RoutingRulesets.IgnoreQueryFilters().AsNoTracking();
        if (!query.Access.IsUnrestricted)
            source = source.Where(x => query.Access.MerchantIds.Contains(x.MerchantId));
        if (query.MerchantId is { } merchantId)
            source = source.Where(x => x.MerchantId == merchantId);
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = ParseRulesetStatus(query.Status);
            source = source.Where(x => x.Status == status);
        }
        var total = await PlatformReadGuard.ReadAsync(ct => source.LongCountAsync(ct), cancellationToken);
        var rows = await PlatformReadGuard.ReadAsync(ct => source.Include(x => x.Rules)
            .OrderBy(x => x.MerchantId).ThenByDescending(x => x.UpdatedAt).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct), cancellationToken);
        return new PagedResult<RoutingRulesetView>(
            rows.Select(ProjectRuleset).ToList(), query.Page, query.Limit, total);
    }

    public async Task<RoutingRulesetView?> GetRulesetAsync(
        Guid rulesetId, Guid? merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => db.RoutingRulesets.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Rules)
            .SingleOrDefaultAsync(x => x.Id == rulesetId && (merchantId == null || x.MerchantId == merchantId), ct),
            cancellationToken);
        return row is null || !access.Allows(row.MerchantId) ? null : ProjectRuleset(row);
    }

    public async Task<RoutingRulesetView> CreateRulesetAsync(
        CreateRoutingRulesetIntent intent, CancellationToken cancellationToken)
    {
        EnsureAccess(intent.Access, intent.MerchantId);
        await ValidateRulesAsync(intent.MerchantId, intent.Rules, cancellationToken);
        var entity = RoutingRuleset.Create(intent.MerchantId, intent.Name, Specs(intent.Rules), clock.UtcNow);
        db.RoutingRulesets.Add(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ProjectRuleset(entity);
    }

    public async Task<RoutingRulesetView> ReplaceRulesetAsync(
        ReplaceRoutingRulesetIntent intent, CancellationToken cancellationToken)
    {
        EnsureAccess(intent.Access, intent.MerchantId);
        var entity = await LoadRulesetAsync(intent.RulesetId, intent.MerchantId, cancellationToken);
        EnsureVersion(entity.Version, intent.ExpectedVersion);
        await ValidateRulesAsync(intent.MerchantId, intent.Rules, cancellationToken);
        entity.Replace(intent.Name, Specs(intent.Rules), clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ProjectRuleset(entity);
    }

    public async Task DeleteRulesetAsync(
        Guid rulesetId, Guid merchantId, long expectedVersion, AdminPaymentsAccess access,
        CancellationToken cancellationToken)
    {
        EnsureAccess(access, merchantId);
        var entity = await LoadRulesetAsync(rulesetId, merchantId, cancellationToken);
        EnsureVersion(entity.Version, expectedVersion);
        if (entity.Status != RoutingRulesetStatus.Draft)
            throw new InvalidOperationException("Only draft routing rulesets can be deleted.");
        db.RoutingRulesets.Remove(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public Task<RoutingActivationResult> RequestActivationAsync(
        RequestRoutingActivationIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            var entity = await LoadRulesetAsync(intent.RulesetId, intent.MerchantId, ct);
            EnsureVersion(entity.Version, intent.ExpectedVersion);
            var input = entity.Rules.Select(x => new RoutingRuleInput(
                x.Priority, x.Method, x.OriginatorId, x.MinAmount, x.MaxAmount,
                x.TargetConnectionId, x.FallbackConnectionId, x.Enabled)).ToList();
            await ValidateRulesAsync(intent.MerchantId, input, ct);
            var intentHash = Hash(new
            {
                intent.RulesetId,
                intent.MerchantId,
                intent.ExpectedVersion,
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "routing.activation", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return Replay<RoutingActivationResult>(prior);

            var approvalId = Guid.CreateVersion7();
            entity.RequestActivation(approvalId, clock.UtcNow);
            var result = new RoutingActivationResult(approvalId, ProjectRuleset(entity), false);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "routing.activation", intent.IdempotencyKey, intentHash);
            operation.Succeed(202, JsonSerializer.Serialize(result, Json), approvalId.ToString("D"));
            EnqueueApproval(new ApprovalRequested(
                Guid.CreateVersion7(), approvalId, "merchant", intent.MerchantId,
                "routing.activate", "settings.manage", intent.Access.ActorId,
                "routing-ruleset", entity.Id.ToString("D"), $"v{entity.Version}",
                intent.CorrelationId, clock.UtcNow));
            await unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    private async Task ValidateRulesAsync(Guid merchantId, IReadOnlyList<RoutingRuleInput> rules, CancellationToken ct)
    {
        var specs = Specs(rules);
        RoutingRuleset.Validate(specs);
        var connectionIds = specs.Select(x => x.TargetConnectionId)
            .Concat(specs.Where(x => x.FallbackConnectionId.HasValue).Select(x => x.FallbackConnectionId!.Value))
            .ToHashSet();
        var connections = await PlatformReadGuard.ReadAsync(token => db.PspConnections.IgnoreQueryFilters()
            .AsNoTracking().Where(x => x.MerchantId == merchantId && connectionIds.Contains(x.Id))
            .ToListAsync(token), ct);
        if (connections.Count != connectionIds.Count)
            throw new InvalidRequestException("Routing references an unknown PSP connection.", "routing_invalid");

        foreach (var rule in specs.Where(x => x.Enabled))
        {
            ValidateEligible(connections.Single(x => x.Id == rule.TargetConnectionId), rule.Method);
            if (rule.FallbackConnectionId is { } fallback)
                ValidateEligible(connections.Single(x => x.Id == fallback), rule.Method);
        }

        var originatorIds = specs.Where(x => x.OriginatorId.HasValue).Select(x => x.OriginatorId!.Value).ToHashSet();
        if (originatorIds.Count > 0)
        {
            var count = await PlatformReadGuard.ReadAsync(token => db.Originators.IgnoreQueryFilters()
                .AsNoTracking().CountAsync(
                    x => x.MerchantId == merchantId && originatorIds.Contains(x.Id), token), ct);
            if (count != originatorIds.Count)
                throw new InvalidRequestException("Routing references an unknown originator.", "routing_invalid");
        }
    }

    private void ValidateEligible(Connection connection, string method)
    {
        if (!connection.IsEnabled)
            throw new InvalidRequestException("Routing references a disabled PSP connection.", "routing_invalid");
        var adapter = adapterFactory.For(connection.Psp);
        if (method == "any")
        {
            if (!connection.EnabledMethods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(adapter.SupportedMethods.Contains))
                throw new InvalidRequestException("Routing connection has no eligible method.", "routing_invalid");
            return;
        }
        if (!connection.Supports(method) || !adapter.SupportedMethods.Contains(method))
            throw new InvalidRequestException("Routing connection does not support the selected method.", "routing_invalid");
    }

    private async Task<PspConnectionView> ProjectConnectionAsync(Connection x, CancellationToken ct)
    {
        var metadata = ReadMetadata(x.Metadata);
        var masked = new Dictionary<string, string>(metadata.Hints, StringComparer.Ordinal);
        if (x.ActiveSecretVersionId is { } versionId)
        {
            var encoded = await vault.MaskedVersionAsync(x.MerchantId, versionId, ct);
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                var hints = JsonSerializer.Deserialize<Dictionary<string, string>>(encoded, Json) ?? [];
                masked = hints.ToDictionary(k => k.Key, v => Mask(v.Value), StringComparer.Ordinal);
            }
            else
            {
                foreach (var key in masked.Keys.ToList())
                    masked[key] = Mask(masked[key]);
            }
        }
        else
        {
            foreach (var key in masked.Keys.ToList())
                masked[key] = Mask(masked[key]);
        }
        var adapter = adapterFactory.For(x.Psp);
        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["test"] = true,
            ["paymentRedirect"] = adapter.SupportedMethods.Count > 0,
            ["capture"] = false,
            ["void"] = false,
            ["refund"] = false,
            ["receipt"] = false,
        };
        return new PspConnectionView(
            x.Id, x.MerchantId, x.Psp.ToCode(),
            x.EnabledMethods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            metadata.Config, masked, x.IsEnabled, HealthCode(x.Health), x.LastTestedAt,
            x.LastTestResult, capabilities, x.CreatedAt, x.Version);
    }

    private async Task<PspConnectionView> ReplayConnectionAsync(AdminOperationRecord record, CancellationToken ct)
    {
        var stored = Replay<PspConnectionView>(record);
        return await GetConnectionAsync(stored.PspConnectionId, stored.MerchantId,
            new AdminPaymentsAccess(record.ActorId, true, new HashSet<Guid>()), ct) ?? stored;
    }

    private AdminOperationRecord BeginOperation(Guid merchantId, Guid actorId, string operation, string key, string hash)
    {
        var record = AdminOperationRecord.Create(merchantId, actorId, operation, key, hash, clock.UtcNow);
        db.AdminOperationRecords.Add(record);
        return record;
    }

    private async Task<AdminOperationRecord?> FindOperationAsync(
        Guid merchantId, Guid actorId, string operation, string key, string hash, CancellationToken ct)
    {
        ValidateKey(key);
        var record = await PlatformReadGuard.ReadAsync(token => db.AdminOperationRecords.IgnoreQueryFilters()
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.MerchantId == merchantId && x.ActorId == actorId && x.Operation == operation
                && x.IdempotencyKey == key, token), ct);
        if (record is null)
            return null;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.IntentHash), Encoding.ASCII.GetBytes(hash)))
            throw new ConflictException("Idempotency key was reused with a different intent.", "idempotency_key_reused");
        if (record.State != AdminOperationState.Succeeded || record.Result is null)
            throw new ConflictException("The operation is still in progress or has an unknown outcome.", "operation_in_progress");
        return record;
    }

    private void EnqueueApproval(ApprovalRequested message)
    {
        db.OutboxMessages.Add(OutboxMessage.Create(
            message.EventId, message.MerchantId!.Value, ApprovalRequested.EventType,
            ApprovalRequested.SchemaVersion, JsonSerializer.Serialize(message, Json), message.OccurredAt));
    }

    private async Task<Connection> LoadConnectionAsync(Guid connectionId, Guid merchantId, CancellationToken ct) =>
        await PlatformReadGuard.ReadAsync(token => db.PspConnections.IgnoreQueryFilters().SingleOrDefaultAsync(
            x => x.Id == connectionId && x.MerchantId == merchantId, token), ct)
        ?? throw new NotFoundException("PSP connection was not found.");

    private async Task<RoutingRuleset> LoadRulesetAsync(Guid rulesetId, Guid merchantId, CancellationToken ct) =>
        await PlatformReadGuard.ReadAsync(token => db.RoutingRulesets.IgnoreQueryFilters().Include(x => x.Rules)
            .SingleOrDefaultAsync(x => x.Id == rulesetId && x.MerchantId == merchantId, token), ct)
        ?? throw new NotFoundException("Routing ruleset was not found.");

    private async Task EnsureMerchantExistsAsync(Guid merchantId, CancellationToken ct)
    {
        if (!await PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters()
                .AnyAsync(x => x.Id == merchantId, token), ct))
            throw new NotFoundException("Merchant was not found.");
    }

    private static IReadOnlyList<RoutingRuleSpec> Specs(IReadOnlyList<RoutingRuleInput> rules) =>
        rules.Select(x => new RoutingRuleSpec(x.Priority, x.Method, x.OriginatorId, x.MinAmount,
            x.MaxAmount, x.TargetConnectionId, x.FallbackConnectionId, x.Enabled)).ToList();

    private static RoutingRulesetView ProjectRuleset(RoutingRuleset x) => new(
        x.Id, x.MerchantId, x.Name, RulesetStatusCode(x.Status), x.ApprovalId,
        x.Rules.OrderBy(r => r.Priority).Select(r => new RoutingRuleView(
            r.Id, r.Priority, r.Method, r.OriginatorId,
            FormatAmount(r.MinAmount), FormatAmount(r.MaxAmount), r.TargetConnectionId,
            r.FallbackConnectionId, r.Enabled)).ToList(),
        x.CreatedAt, x.UpdatedAt, x.Version);

    private static T Replay<T>(AdminOperationRecord record) =>
        JsonSerializer.Deserialize<T>(record.Result!, Json)
        ?? throw new InvalidOperationException("Stored operation result is invalid.");

    private static (string? PspMerchantId, JsonElement? Config, Dictionary<string, string> Hints) ReadMetadata(string? value)
    {
        var hints = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
            return (null, null, hints);
        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        var merchantId = root.TryGetProperty("merchantId", out var mid) && mid.ValueKind == JsonValueKind.String
            ? mid.GetString() : null;
        JsonElement? config = root.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object
            ? cfg.Clone() : null;
        if (root.TryGetProperty("secretHints", out var secretHints) && secretHints.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in secretHints.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String)
                    hints[property.Name] = property.Value.GetString() ?? string.Empty;
        }
        return (merchantId, config, hints);
    }

    private static string ConnectionMetadata(
        string? merchantId, JsonElement? config, IReadOnlyDictionary<string, string> hints) =>
        JsonSerializer.Serialize(new { merchantId, config, secretHints = hints }, Json);

    internal static void ValidateConfig(JsonElement? config)
    {
        if (config is null)
            return;
        if (config.Value.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException("PSP config must be an object.", "invalid_psp_config");
        if (config.Value.GetRawText().Length > 16_384)
            throw new InvalidRequestException("PSP config is too large.", "invalid_psp_config");

        foreach (var property in config.Value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "accountId":
                    ValidateConfigString(property.Value, property.Name, 200);
                    break;
                case "card":
                case "installment":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw InvalidConfigField(property.Name);
                    break;
                case "enabledSources":
                    ValidateConfigStrings(property.Value, property.Name, 20, 50, requireHttps: false);
                    break;
                case "returnUrls":
                    ValidateConfigStrings(property.Value, property.Name, 10, 2_048, requireHttps: true);
                    break;
                default:
                    throw new InvalidRequestException("PSP config contains a non-allowlisted field.", "invalid_psp_config");
            }
        }
    }

    private static void ValidateConfigStrings(
        JsonElement value, string name, int maxItems, int maxLength, bool requireHttps)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw InvalidConfigField(name);
        var items = value.EnumerateArray().ToList();
        if (items.Count > maxItems)
            throw InvalidConfigField(name);
        foreach (var item in items)
        {
            ValidateConfigString(item, name, maxLength);
            if (requireHttps && (!Uri.TryCreate(item.GetString(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment)))
                throw InvalidConfigField(name);
        }
    }

    private static void ValidateConfigString(JsonElement value, string name, int maxLength)
    {
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || value.GetString()!.Length > maxLength
            || value.GetString()!.Any(char.IsControl))
            throw InvalidConfigField(name);
    }

    private static InvalidRequestException InvalidConfigField(string name) =>
        new($"PSP config field '{name}' is invalid.", "invalid_psp_config");

    private IReadOnlyList<string> ValidateMethods(Code psp, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            throw new InvalidRequestException("At least one payment method is required.", "invalid_psp_config");
        var methods = values.Select(x => x.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        var supported = adapterFactory.For(psp).SupportedMethods;
        if (methods.Any(x => !supported.Contains(x)))
            throw new InvalidRequestException("PSP method is not supported by the adapter.", "invalid_psp_config");
        return methods;
    }

    private static Code ParsePsp(string value)
    {
        try { return Codes.FromCode(value.Trim().ToLowerInvariant()); }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        { throw new InvalidRequestException("PSP code is invalid.", "invalid_psp_config"); }
    }

    private static PspConnectionHealth ParseHealth(string value) => value.Trim().ToLowerInvariant() switch
    {
        "unknown" => PspConnectionHealth.Unknown,
        "healthy" => PspConnectionHealth.Healthy,
        "failed" => PspConnectionHealth.Failed,
        _ => throw new InvalidRequestException("PSP health filter is invalid.", "invalid_filter"),
    };

    private static string HealthCode(PspConnectionHealth value) => value switch
    {
        PspConnectionHealth.Unknown => "unknown",
        PspConnectionHealth.Healthy => "healthy",
        PspConnectionHealth.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static RoutingRulesetStatus ParseRulesetStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "draft" => RoutingRulesetStatus.Draft,
        "pending" => RoutingRulesetStatus.PendingApproval,
        "active" => RoutingRulesetStatus.Active,
        "superseded" => RoutingRulesetStatus.Superseded,
        _ => throw new InvalidRequestException("Routing status filter is invalid.", "invalid_filter"),
    };

    private static string RulesetStatusCode(RoutingRulesetStatus value) => value switch
    {
        RoutingRulesetStatus.Draft => "draft",
        RoutingRulesetStatus.PendingApproval => "pending",
        RoutingRulesetStatus.Active => "active",
        RoutingRulesetStatus.Superseded => "superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string? FormatAmount(decimal? value) => value?.ToString("0.00##", CultureInfo.InvariantCulture);
    private static string Mask(string value) => value.StartsWith("****", StringComparison.Ordinal) ? value : $"****{value}";

    private static void EnsureAccess(AdminPaymentsAccess access, Guid merchantId)
    {
        if (!access.Allows(merchantId))
            throw new AdminPaymentsAccessDeniedException("Merchant is outside the current admin scope.");
    }

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected)
            throw new ConcurrencyConflictException("The resource version is stale.");
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || key.Any(char.IsControl))
            throw new InvalidRequestException("Idempotency-Key is invalid.", "validation_failed");
    }

    private static string Hash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)))).ToLowerInvariant();

    internal static string SecretIntentFingerprint(string envelopeJson) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(envelopeJson))).ToLowerInvariant();
}
