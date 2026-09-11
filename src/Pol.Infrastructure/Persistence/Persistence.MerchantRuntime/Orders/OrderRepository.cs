using BuildingBlocks.Application;
using Checkouts.Application;
using Checkouts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orders.Application;
using Orders.Domain;
using Platform.Application.Transactions;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>
/// EF Core implementation of <see cref="IOrderRepository"/> over the MerchantRuntime data plane.
/// Reads/writes go through <c>CommerceDbContext.Set&lt;Order&gt;()</c>; merchant isolation is
/// enforced by the query filter + sealed write guard. Saving is the caller's responsibility via
/// <c>IUnitOfWork</c>. Scoped — depends on the Scoped DbContext.
/// </summary>
internal sealed class OrderRepository : IOrderRepository, IOrderStore, IOrderWorkflowStore, IPaymentLinkStore,
    IPaymentLinkReplayStore, ICheckoutTransactionStore, ITransactionRepository
{
    private readonly CommerceDbContext _db;
    private readonly ILogger<OrderRepository> _logger;

    public OrderRepository(
        CommerceDbContext db,
        ILogger<OrderRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    internal OrderRepository(CommerceDbContext db)
        : this(db, NullLogger<OrderRepository>.Instance) { }

    public Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Order>()
            .Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct), cancellationToken);

    public async Task<Order?> GetForUpdateAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (_db.Database.IsSqlServer())
        {
            var locked = await PlatformReadGuard.ReadAsync(ct => _db.Database
                .SqlQueryRaw<Guid>(
                    "SELECT Id AS Value FROM shop.Orders WITH (UPDLOCK,HOLDLOCK) WHERE Id = @p0 AND MerchantId = @p1",
                    new SqlParameter("@p0", orderId),
                    new SqlParameter("@p1", _db.CurrentMerchant))
                .ToListAsync(ct), cancellationToken).ConfigureAwait(false);
            if (locked.Count == 0)
                return null;
        }

        return await PlatformReadGuard.ReadAsync(ct => _db.Set<Order>()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OrderStatusTotal>> GetReconciliationAsync(Guid merchantId, CancellationToken cancellationToken) =>
        await PlatformReadGuard.ReadAsync(ct => _db.Set<Order>()
            .Where(o => o.MerchantId == merchantId)
            .GroupBy(o => new { o.Status, o.Amount.Currency })
            .Select(g => new OrderStatusTotal(g.Key.Status, g.Key.Currency, g.Count(), g.Sum(o => o.Amount.Amount)))
            .ToListAsync(ct), cancellationToken);

    public async Task<PagedResult<Order>> ListAsync(
        Guid merchantId,
        PagedQuery query,
        CancellationToken cancellationToken)
    {
        var source = _db.Set<Order>()
            .AsNoTracking()
            .Where(o => o.MerchantId == merchantId)
            .ApplyFilters(query.Filters, _logger);

        if (_db.CurrentIdentityAuthorization is { } identityAuthorization)
        {
            source = identityAuthorization.DataScope switch
            {
                Access.Domain.DataScope.Merchant => source,
                Access.Domain.DataScope.Self when identityAuthorization.AccountType == Accounts.Domain.AccountType.Agent
                    && identityAuthorization.AgentSaleId is { } saleId => source.Where(o => o.OwnerSaleId == saleId),
                Access.Domain.DataScope.Branch when identityAuthorization.BranchIds.Count == 1
                    => source.Where(o => o.OwnerBranchIdAtCreation == identityAuthorization.BranchIds.Single()),
                Access.Domain.DataScope.AssignedBranches when identityAuthorization.BranchIds.Count > 0
                    => source.Where(o => o.OwnerBranchIdAtCreation.HasValue
                        && identityAuthorization.BranchIds.Contains(o.OwnerBranchIdAtCreation.Value)),
                _ => source.Where(_ => false),
            };
        }
        var total = await PlatformReadGuard.ReadAsync(
            ct => source.LongCountAsync(ct), cancellationToken).ConfigureAwait(false);
        var skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);
        var items = await PlatformReadGuard.ReadAsync(ct => source
                .ApplySort(query.Sort, _logger)
                .Skip(skip)
                .Take(query.Limit)
                .Include(o => o.Items)
                .AsSplitQuery()
                .ToListAsync(ct), cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<Order>(items, query.Page, query.Limit, total);
    }

    public void Add(Order order) => _db.Set<Order>().Add(order);

    public Task<Order?> GetAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Order>()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.MerchantId == merchantId, ct), cancellationToken);

    public async Task<Order?> GetForUpdateAsync(
        Guid merchantId, Guid orderId, CancellationToken cancellationToken)
    {
        if (_db.Database.IsSqlServer())
        {
            var locked = await PlatformReadGuard.ReadAsync(ct => _db.Database
                .SqlQueryRaw<Guid>(
                    "SELECT Id AS Value FROM shop.Orders WITH (UPDLOCK,HOLDLOCK) WHERE Id = @p0 AND MerchantId = @p1",
                    new SqlParameter("@p0", orderId),
                    new SqlParameter("@p1", merchantId))
                .ToListAsync(ct), cancellationToken).ConfigureAwait(false);
            if (locked.Count == 0)
                return null;
        }

        return await GetAsync(merchantId, orderId, cancellationToken).ConfigureAwait(false);
    }

    public Task<PaymentLink?> GetByHashAsync(
        Guid merchantId, byte[] tokenHash, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<PaymentLink>()
            .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.TokenHash == tokenHash, ct), cancellationToken);

    public Task<PaymentLink?> GetLinkAsync(
        Guid merchantId, Guid linkId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<PaymentLink>()
            .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.Id == linkId, ct), cancellationToken);

    public Task<PaymentLink?> GetActiveForOrderAsync(
        Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<PaymentLink>()
            .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.OrderId == orderId
                && x.Status == PaymentLinkStatus.Active, ct), cancellationToken);

    public async Task<IReadOnlyList<PaymentLink>> ListForOrderAsync(
        Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
        await PlatformReadGuard.ReadAsync(ct => _db.Set<PaymentLink>()
            .AsNoTracking()
            .Where(x => x.MerchantId == merchantId && x.OrderId == orderId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct), cancellationToken).ConfigureAwait(false);

    public Task<PaymentLinkReplay?> FindReplayAsync(
        Guid merchantId, string operation, string idempotencyKey, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<PaymentLinkReplay>()
            .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.Operation == operation
                && x.IdempotencyKey == idempotencyKey, ct), cancellationToken);

    public void Add(PaymentLink link) => _db.Set<PaymentLink>().Add(link);

    public void Add(PaymentLinkReplay replay) => _db.Set<PaymentLinkReplay>().Add(replay);

    public async Task<CheckoutTransactionContext?> GetCheckoutForUpdateAsync(
        Guid merchantId, Guid orderId, Guid linkId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
            return null;
        if (_db.Database.IsSqlServer())
        {
            var locked = await PlatformReadGuard.ReadAsync(ct => _db.Database
                .SqlQueryRaw<Guid>(
                    "SELECT Id AS Value FROM shop.Orders WITH (UPDLOCK,HOLDLOCK) WHERE Id = @p0 AND (@p1 = '00000000-0000-0000-0000-000000000000' OR MerchantId = @p1)",
                    new SqlParameter("@p0", orderId),
                    new SqlParameter("@p1", merchantId))
                .ToListAsync(ct), cancellationToken).ConfigureAwait(false);
            if (locked.Count == 0)
                return null;
        }

        var orderQuery = _db.Set<Order>().IgnoreQueryFilters()
            .Include(x => x.Items)
            .Where(x => x.Id == orderId);
        if (merchantId != Guid.Empty)
            orderQuery = orderQuery.Where(x => x.MerchantId == merchantId);
        var order = await PlatformReadGuard.ReadAsync(
            ct => orderQuery.SingleOrDefaultAsync(ct), cancellationToken).ConfigureAwait(false);
        if (order is null)
            return null;

        if (linkId == Guid.Empty)
            return new CheckoutTransactionContext(order, null);
        var link = await PlatformReadGuard.ReadAsync(ct => _db.Set<PaymentLink>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == linkId && x.OrderId == orderId && x.MerchantId == order.MerchantId, ct),
            cancellationToken).ConfigureAwait(false);
        return new CheckoutTransactionContext(order, link);
    }

    public void Add(Transaction transaction) => _db.Set<Transaction>().Add(transaction);

    public void AddEvent(TransactionEvent transactionEvent) => _db.Set<TransactionEvent>().Add(transactionEvent);

    public Task<Transaction?> GetByIdAsync(
        Guid merchantId, Guid transactionId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Transaction>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == transactionId && x.MerchantId == merchantId, ct), cancellationToken);

    public async Task<PagedResult<Transaction>> ListAsync(
        Guid merchantId, int page, int limit, string? status, CancellationToken cancellationToken)
    {
        if (page < 1 || limit is < 1 or > 100)
            throw new InvalidRequestException("Page and limit are invalid.", "invalid_filter");
        var source = _db.Set<Transaction>().IgnoreQueryFilters().AsNoTracking().Where(x => x.MerchantId == merchantId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<TransactionStatus>(status.Trim(), ignoreCase: true, out var parsed))
                throw new InvalidRequestException("Transaction status is invalid.", "invalid_filter");
            source = source.Where(x => x.Status == parsed);
        }
        var total = await PlatformReadGuard.ReadAsync(ct => source.LongCountAsync(ct), cancellationToken);
        var rows = await PlatformReadGuard.ReadAsync(ct => source
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * limit).Take(limit).ToListAsync(ct), cancellationToken);
        return new PagedResult<Transaction>(rows, page, limit, total);
    }

    public Task<Transaction?> GetForUpdateTransactionAsync(
        Guid merchantId, Guid transactionId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Transaction>()
            .FirstOrDefaultAsync(x => x.Id == transactionId && x.MerchantId == merchantId, ct), cancellationToken);

    public Task<Transaction?> GetPotentialForOrderAsync(
        Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Transaction>()
            .Where(x => x.MerchantId == merchantId && x.OrderId == orderId
                && (x.Status == TransactionStatus.Created || x.Status == TransactionStatus.PendingConfirmation))
            .OrderByDescending(x => x.AttemptNo)
            .FirstOrDefaultAsync(ct), cancellationToken);

    public Task<Transaction?> GetLatestForOrderAsync(
        Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Transaction>()
            .Where(x => x.MerchantId == merchantId && x.OrderId == orderId)
            .OrderByDescending(x => x.AttemptNo)
            .FirstOrDefaultAsync(ct), cancellationToken);

    public Task<Transaction?> GetByProviderReferenceAsync(
        Guid merchantId, Guid providerAccountId, PspEnvironment? environment, string reference,
        CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Transaction>()
            .FirstOrDefaultAsync(x => x.MerchantId == merchantId
                && x.ProviderAccountId == providerAccountId
                && (environment == null || x.Environment == environment)
                && (x.ProviderReference == reference || x.ProviderRequestReference == reference), ct),
            cancellationToken);

    public Task<Transaction?> GetByReturnBindingAsync(
        string returnBinding, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Transaction>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.ReturnBinding == returnBinding, ct), cancellationToken);

    public async Task<IReadOnlyList<(Guid MerchantId, Guid TransactionId)>> ListDueAsync(
        DateTime now, int limit, CancellationToken cancellationToken) =>
        (await PlatformReadGuard.ReadAsync(ct => _db.Set<Transaction>().IgnoreQueryFilters()
            .Where(x => x.NextInquiryAt != null && x.NextInquiryAt <= now
                && (x.Status == TransactionStatus.Created || x.Status == TransactionStatus.PendingConfirmation))
            .OrderBy(x => x.NextInquiryAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(x => new DueTransactionRow(x.MerchantId, x.Id))
            .ToListAsync(ct), cancellationToken)
            .ConfigureAwait(false))
            .Select(x => (x.MerchantId, x.TransactionId)).ToList();

    private sealed record DueTransactionRow(Guid MerchantId, Guid TransactionId);

    public async Task<int> NextAttemptNoAsync(
        Guid merchantId, Guid orderId, CancellationToken cancellationToken)
    {
        var latest = await PlatformReadGuard.ReadAsync(ct => _db.Set<Transaction>()
            .Where(x => x.MerchantId == merchantId && x.OrderId == orderId)
            .Select(x => (int?)x.AttemptNo)
            .MaxAsync(ct), cancellationToken).ConfigureAwait(false);
        return (latest ?? 0) + 1;
    }

    public Task<bool> EventExistsAsync(
        Guid merchantId, Guid transactionId, string source, string eventReference,
        CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<TransactionEvent>()
            .AnyAsync(x => x.MerchantId == merchantId && x.TransactionId == transactionId
                && x.Source == source && x.EventReference == eventReference, ct), cancellationToken);

    public async Task<IReadOnlyList<TransactionEvent>> ListEventsAsync(
        Guid merchantId, Guid transactionId, CancellationToken cancellationToken) =>
        await PlatformReadGuard.ReadAsync(ct => _db.Set<TransactionEvent>().IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MerchantId == merchantId && x.TransactionId == transactionId)
            .OrderBy(x => x.ReceivedAt)
            .ToListAsync(ct), cancellationToken).ConfigureAwait(false);
}
