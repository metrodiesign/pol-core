using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.Logging;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Tests;

internal sealed class FakePaymentAuthorizationLocks : IPaymentAuthorizationLockManager
{
    public int MerchantSharedCalls { get; private set; }
    public Task AcquireGlobalExclusiveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task AcquireMerchantSharedAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        MerchantSharedCalls++;
        return Task.CompletedTask;
    }
    public Task AcquireMerchantExclusiveAsync(Guid merchantId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class FakeEffectivePaymentCapabilities(
    PaymentCapabilityDenial denial = PaymentCapabilityDenial.None) : IEffectivePaymentCapabilityResolver
{
    public int ResolveCalls { get; private set; }

    public Task<PaymentMethodDecision> ResolveMethodAsync(
        ResolvePaymentMethod request, CancellationToken cancellationToken)
    {
        ResolveCalls++;
        return Task.FromResult(denial == PaymentCapabilityDenial.None
            ? new PaymentMethodDecision(true, request.Method, denial, Guid.NewGuid())
            : new PaymentMethodDecision(false, request.Method, denial, null));
    }

    public Task<IReadOnlyList<EffectivePaymentMethod>> ListMethodsAsync(
        PaymentCapabilitySubject subject, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EffectivePaymentMethod>>([]);

    public Task<IReadOnlyList<EffectivePaymentOption>> ResolveOptionsAsync(
        ResolvePaymentMethod request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EffectivePaymentOption>>([]);
}

/// <summary>
/// First set of in-memory doubles for the Payments handlers (mirrors <c>tests/Carts.Tests/Fakes.cs</c> and
/// <c>tests/Orders.Tests/Fakes.cs</c>). The adapter tests drive the REAL adapters over
/// <see cref="Psp.PspTestHttp"/>, so nothing here fakes HTTP — these exist purely so a handler's decision
/// sequence can be exercised without a DB.
/// </summary>
internal sealed class FakePayableOrderReader : IPayableOrderReader
{
    private readonly PayableOrder? _order;

    /// <summary>Pass null for "no such order under this merchant" — the query filter's own answer for both a
    /// missing order and another company's order.</summary>
    public FakePayableOrderReader(PayableOrder? order = null) => _order = order;

    public int Calls { get; private set; }

    public int LockedCalls { get; private set; }
    public Guid? AttachedPaymentSessionId { get; private set; }

    /// <summary>What the LOCKED re-read reports, when it should differ from <see cref="GetAsync"/> — how a
    /// cancel that landed between the two reads is simulated. Null = same order as the first read.</summary>
    public Func<Guid, PayableOrder?>? OnGetForMint { get; set; }

    public Task<PayableOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_order?.OrderId == orderId ? _order : null);
    }

    public Task<PayableOrder?> GetForMintAsync(Guid orderId, CancellationToken cancellationToken)
    {
        LockedCalls++;
        return OnGetForMint is { } locked
            ? Task.FromResult(locked(orderId))
            : Task.FromResult(_order?.OrderId == orderId ? _order : null);
    }

    public Task AttachAttemptAsync(
        Guid orderId,
        Guid paymentSessionId,
        CancellationToken cancellationToken)
    {
        AttachedPaymentSessionId = paymentSessionId;
        return Task.CompletedTask;
    }

    /// <summary>The document keys the sold-check probes for this order (products-external-source-of-truth
    /// REQ-5.6). Empty by default; a test that exercises the pre-charge sold-check sets it.</summary>
    public IReadOnlyList<DocumentKey> DocumentKeys { get; set; } = [];

    public Task<IReadOnlyList<DocumentKey>> GetDocumentKeysAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(DocumentKeys);
}

/// <summary>The pre-charge sold-check double (products-external-source-of-truth REQ-5.6). Returns nothing by
/// default (every document sellable); a test that wants a 409 seeds <see cref="Statuses"/>.</summary>
internal sealed class FakeDocumentSaleProbe : IDocumentSaleProbe
{
    public IReadOnlyList<DocumentSaleStatus> Statuses { get; set; } = [];

    public Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
        IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken) =>
        Task.FromResult(Statuses);
}

internal sealed class FakeConnectionRepository : IConnectionRepository
{
    private readonly List<Connection> _connections = [];

    public FakeConnectionRepository(params Connection[] seed) => _connections.AddRange(seed);

    public Task<Connection?> GetAsync(Guid merchantId, Code psp, CancellationToken cancellationToken) =>
        Task.FromResult(_connections.FirstOrDefault(c => c.MerchantId == merchantId && c.Psp == psp));

    public Task<Connection?> GetByIdAsync(Guid pspConnectionId, CancellationToken cancellationToken) =>
        Task.FromResult(_connections.FirstOrDefault(c => c.Id == pspConnectionId));

    public void Add(Connection connection) => _connections.Add(connection);

    public Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Connection>>(_connections.Where(c => c.MerchantId == merchantId).ToList());
}

/// <summary>Declares a capability set without speaking to a PSP. Every charge/webhook member throws (or
/// refuses) unless a test opts in through <see cref="OnCreateCharge"/>/<see cref="OnFetchCharge"/>/
/// <see cref="ParsedWebhook"/>/<see cref="WebhookVerifies"/>: a handler test that reaches one it did not
/// arrange has escaped the guards it was written to prove.</summary>
internal sealed class FakePspAdapter : IPspAdapter
{
    public FakePspAdapter(Code psp, params string[] supportedMethods)
    {
        Psp = psp;
        SupportedMethods = supportedMethods.ToHashSet(StringComparer.Ordinal);
    }

    public Code Psp { get; }

    public IReadOnlySet<string> SupportedMethods { get; }

    /// <summary>Drives the charge call: return a hosted charge, or throw to stand in for the PSP refusing.</summary>
    public Func<Session, PspCharge>? OnCreateCharge { get; init; }

    /// <summary>The connection id the last charge call was handed — the value a real adapter turns into the
    /// per-connection webhook URL, so a handler that passed the wrong one (or none) is visible here.</summary>
    public Guid ChargedConnectionId { get; private set; }

    public Task<PspCharge> CreateRedirectChargeAsync(
        Session session, Guid pspConnectionId, string secret, CancellationToken cancellationToken)
    {
        ChargedConnectionId = pspConnectionId;
        return OnCreateCharge is null
            ? throw new NotSupportedException("This fake never charges.")
            : Task.FromResult(OnCreateCharge(session));
    }

    /// <summary>Drives fetch-to-confirm: the status + amount the PSP reports for the queried charge. A null
    /// Amount stands in for a PSP whose response carries no amount (REQ-8.3).</summary>
    public Func<string, PspChargeConfirmation>? OnFetchCharge { get; init; }

    /// <summary>What <see cref="VerifyWebhook"/> answers. False by default so a test must opt in — a handler
    /// that stopped verifying signatures cannot slip through on a permissive default.</summary>
    public bool WebhookVerifies { get; init; }

    /// <summary>The event <see cref="ParseWebhook"/> yields. Null (the default) throws.</summary>
    public WebhookEvent? ParsedWebhook { get; init; }

    public bool VerifyWebhook(string rawPayload, string signature, string secret) => WebhookVerifies;

    public Task<PspChargeConfirmation> FetchChargeAsync(string externalChargeId, string secret, CancellationToken cancellationToken) =>
        OnFetchCharge is null
            ? throw new NotSupportedException("This fake never fetches charges.")
            : Task.FromResult(OnFetchCharge(externalChargeId));

    public WebhookEvent ParseWebhook(string rawPayload) =>
        ParsedWebhook ?? throw new NotSupportedException("This fake never parses webhooks.");
}

/// <summary>Claims each key set once, in memory. <see cref="Claims"/> exposes what was claimed so a test can
/// tell "the transition was refused" from "the claim never happened".</summary>
internal sealed class FakeIdempotencyStore : IIdempotencyStore
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public List<string> Claims { get; } = [];

    public Task<bool> TryBeginAsync(IReadOnlyCollection<string> keys, string context, CancellationToken cancellationToken)
    {
        if (keys.Any(k => _seen.Contains($"{context}:{k}")))
            return Task.FromResult(false);

        foreach (var key in keys)
        {
            _seen.Add($"{context}:{key}");
            Claims.Add(key);
        }

        return Task.FromResult(true);
    }
}

/// <summary>Records what a handler enqueued in its transaction — an outcome that must not publish
/// <c>PaymentPaid</c> has to leave this empty, which asserting on the outcome alone would not prove.</summary>
internal sealed class FakeOutbox : IOutbox
{
    public List<INotification> Enqueued { get; } = [];

    public void Enqueue(INotification notification) => Enqueued.Add(notification);
}

internal sealed class FakePspAdapterFactory : IPspAdapterFactory
{
    private readonly Dictionary<Code, IPspAdapter> _adapters;

    public FakePspAdapterFactory(params IPspAdapter[] adapters) =>
        _adapters = adapters.ToDictionary(a => a.Psp);

    public IPspAdapter For(Code psp) =>
        _adapters.TryGetValue(psp, out var adapter)
            ? adapter
            : throw new ArgumentOutOfRangeException(nameof(psp), psp, "No PSP adapter registered.");
}

internal sealed class FakeSessionRepository : ISessionRepository
{
    private readonly List<Session> _sessions = [];

    public FakeSessionRepository(params Session[] seed) => _sessions.AddRange(seed);

    /// <summary>Sessions handed to <see cref="Add"/> by the code under test — the idempotent-return path
    /// must leave this empty.</summary>
    public List<Session> Added { get; } = [];

    /// <summary>Fires at the moment of the insert. Lets a test observe the world AS the row is added — the
    /// only way to prove the expire-then-mint ORDER rather than just its end state.</summary>
    public Action<Session>? OnAdd { get; init; }

    public void Add(Session session)
    {
        OnAdd?.Invoke(session);
        Added.Add(session);
        _sessions.Add(session);
    }

    public Task<Session?> GetByIdAsync(Guid paymentSessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.FirstOrDefault(s => s.Id == paymentSessionId));

    public Task<PagedResult<Session>> ListAsync(PagedQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new PagedResult<Session>(
            _sessions.Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToList(),
            query.Page,
            query.Limit,
            _sessions.Count));

    public Task<Session?> GetByExternalChargeAsync(Code psp, string externalChargeId, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.FirstOrDefault(s => s.Psp == psp && s.PspExternalChargeId == externalChargeId));

    public Task<Session?> GetOpenForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.FirstOrDefault(s =>
            s.OrderId == orderId && s.Status is SessionStatus.Created or SessionStatus.Redirected));
}

/// <summary>Reveals a fixed plaintext and COUNTS the reveals: a request the guards should have refused must
/// not have touched the vault at all. Every other member throws — nothing on a handler path calls them.</summary>
internal sealed class FakeVaultSecretStore : IVaultSecretStore
{
    private readonly string _secret;

    public FakeVaultSecretStore(string secret = "psp-test-secret") => _secret = secret;

    public int Reveals { get; private set; }

    /// <summary>Set to stand in for a vault that cannot hand back the secret (down, or the ref is gone).</summary>
    public Exception? RevealFails { get; init; }

    public Task<string> RevealAsync(Guid merchantId, string name, CancellationToken cancellationToken)
    {
        Reveals++;
        return RevealFails is null ? Task.FromResult(_secret) : throw RevealFails;
    }

    public Task StoreAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake never writes.");

    public Task InsertAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake never writes.");

    public Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake never masks.");

    public Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake never probes.");
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }
    public int TransactionCount { get; private set; }

    /// <summary>Returns the exception the Nth save (1-based) must throw, or null to let it succeed — how the
    /// concurrency-loser and the "recording the failure itself fails" paths are driven.</summary>
    public Func<int, Exception?>? SaveFails { get; init; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        if (SaveFails?.Invoke(SaveCount) is { } failure)
            throw failure;

        return Task.FromResult(0);
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        TransactionCount++;
        return await operation(cancellationToken);
    }
}

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; init; } = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);
}

/// <summary>Keeps every log line so a test can assert that a money/state disagreement was raised at Critical
/// — the only trace those outcomes leave, since they deliberately change nothing and throw nothing.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IReadOnlyList<string> Critical =>
        Entries.Where(e => e.Level == LogLevel.Critical).Select(e => e.Message).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
