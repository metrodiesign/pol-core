using BuildingBlocks.Application;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Tests;

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

    public Task<PayableOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_order?.OrderId == orderId ? _order : null);
    }
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

/// <summary>Declares a capability set without speaking to a PSP. Every charge/webhook member throws unless a
/// test opts in through <see cref="OnCreateCharge"/>: a handler test that reaches one it did not arrange has
/// escaped the guards it was written to prove.</summary>
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

    public Task<PspCharge> CreateRedirectChargeAsync(Session session, string secret, CancellationToken cancellationToken) =>
        OnCreateCharge is null
            ? throw new NotSupportedException("This fake never charges.")
            : Task.FromResult(OnCreateCharge(session));

    public bool VerifyWebhook(string rawPayload, string signature, string secret) =>
        throw new NotSupportedException("This fake never verifies webhooks.");

    public Task<PspChargeStatus> FetchChargeAsync(string externalChargeId, string secret, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake never fetches charges.");

    public WebhookEvent ParseWebhook(string rawPayload) =>
        throw new NotSupportedException("This fake never parses webhooks.");
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

    public void Add(Session session)
    {
        Added.Add(session);
        _sessions.Add(session);
    }

    public Task<Session?> GetByIdAsync(Guid paymentSessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.FirstOrDefault(s => s.Id == paymentSessionId));

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

    public Task<string> RevealAsync(Guid merchantId, string name, CancellationToken cancellationToken)
    {
        Reveals++;
        return Task.FromResult(_secret);
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

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        await operation(cancellationToken);
}

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; init; } = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);
}
