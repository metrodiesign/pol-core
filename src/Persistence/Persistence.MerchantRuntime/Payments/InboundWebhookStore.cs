using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Payments.Application.HandlePspWebhook;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Persistence.MerchantRuntime.Payments;

internal sealed class InboundWebhookStore(
    MerchantRuntimeDbContext db,
    IUnitOfWork unitOfWork,
    IClock clock) : IInboundWebhookRecorder, IAdminInboundWebhookReader
{
    public async Task RecordRejectedAsync(
        Guid connectionId,
        Guid merchantId,
        string pspCode,
        string payloadFingerprint,
        bool signatureValid,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var externalEventId = $"rejected:{payloadFingerprint}";
        if (await FindEventAsync(connectionId, externalEventId, cancellationToken).ConfigureAwait(false) is not null)
            return;

        var entity = InboundWebhookEvent.Reject(
            connectionId, merchantId, pspCode, payloadFingerprint, signatureValid, failureCode, clock.UtcNow);
        db.Set<InboundWebhookEvent>().Add(entity);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConflictException)
        {
            db.Entry(entity).State = EntityState.Detached;
            if (await FindEventAsync(connectionId, externalEventId, cancellationToken).ConfigureAwait(false) is null)
                throw;
        }
    }

    public async Task<InboundWebhookClaim> ClaimAsync(
        Guid connectionId,
        Guid merchantId,
        string pspCode,
        string externalEventId,
        string payloadFingerprint,
        CancellationToken cancellationToken)
    {
        var existing = await FindEventAsync(connectionId, externalEventId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return Claim(existing);

        var entity = InboundWebhookEvent.Receive(
            connectionId, merchantId, pspCode, externalEventId, payloadFingerprint, clock.UtcNow);
        db.Set<InboundWebhookEvent>().Add(entity);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Claim(entity);
        }
        catch (ConflictException)
        {
            db.Entry(entity).State = EntityState.Detached;
            existing = await FindEventAsync(connectionId, externalEventId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
                throw;
            return Claim(existing);
        }
    }

    public async Task<InboundWebhookEvent> LoadAsync(Guid eventId, CancellationToken cancellationToken) => await PlatformReadGuard.ReadAsync(ct => db.Set<InboundWebhookEvent>()
                .SingleOrDefaultAsync(x => x.Id == eventId, ct), cancellationToken)
            .ConfigureAwait(false)
        ?? throw new NotFoundException("Inbound webhook event was not found.");

    public async Task<PagedResult<InboundWebhookEventView>> ListAsync(
        InboundWebhookQuery query,
        CancellationToken cancellationToken)
    {
        Validate(query);
        if (!query.Access.IsUnrestricted && query.Access.MerchantIds.Count == 0)
            return new PagedResult<InboundWebhookEventView>([], query.Page, query.Limit, 0);

        var source = Scope(db.Set<InboundWebhookEvent>().IgnoreQueryFilters().AsNoTracking(), query.Access);
        if (query.MerchantId is { } merchantId)
            source = source.Where(x => x.MerchantId == merchantId);
        if (!string.IsNullOrWhiteSpace(query.Psp))
        {
            var psp = Codes.FromCode(query.Psp.Trim().ToLowerInvariant()).ToCode();
            source = source.Where(x => x.PspCode == psp);
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim().ToLowerInvariant();
            source = status switch
            {
                "delivered" => source.Where(x => x.Status == InboundWebhookStatus.Processed
                    || x.Status == InboundWebhookStatus.Duplicate),
                "pending" => source.Where(x => x.Status == InboundWebhookStatus.Received
                    || x.Status == InboundWebhookStatus.Ignored),
                "failed" => source.Where(x => x.Status == InboundWebhookStatus.Rejected),
                _ when Enum.TryParse<InboundWebhookStatus>(status, true, out var parsed) =>
                    source.Where(x => x.Status == parsed),
                _ => throw new ArgumentException("Unknown inbound webhook status."),
            };
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(x => x.ExternalEventId.Contains(search)
                || x.PayloadFingerprint.Contains(search));
        }
        if (query.From is { } from)
            source = source.Where(x => x.ReceivedAt >= from);
        if (query.To is { } to)
            source = source.Where(x => x.ReceivedAt <= to);

        var total = await PlatformReadGuard.ReadAsync(ct => source.LongCountAsync(ct), cancellationToken)
            .ConfigureAwait(false);
        var skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);
        var rows = await PlatformReadGuard.ReadAsync(ct => source
                .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.Id)
                .Skip(skip).Take(query.Limit).ToListAsync(ct), cancellationToken)
            .ConfigureAwait(false);
        return new PagedResult<InboundWebhookEventView>(
            rows.Select(Project).ToArray(), query.Page, query.Limit, total);
    }

    public async Task<InboundWebhookEventView?> GetAsync(
        Guid id,
        InboundWebhookAccess access,
        CancellationToken cancellationToken)
    {
        if (!access.IsUnrestricted && access.MerchantIds.Count == 0)
            return null;
        var entity = await PlatformReadGuard.ReadAsync(ct => Scope(
                db.Set<InboundWebhookEvent>().IgnoreQueryFilters().AsNoTracking(), access)
            .SingleOrDefaultAsync(x => x.Id == id, ct), cancellationToken).ConfigureAwait(false);
        return entity is null ? null : Project(entity);
    }

    private Task<InboundWebhookEvent?> FindEventAsync(
        Guid connectionId,
        string externalEventId,
        CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => db.Set<InboundWebhookEvent>()
            .SingleOrDefaultAsync(x => x.PspConnectionId == connectionId
                && x.ExternalEventId == externalEventId, ct), cancellationToken);

    private static IQueryable<InboundWebhookEvent> Scope(
        IQueryable<InboundWebhookEvent> query,
        InboundWebhookAccess access) => access.IsUnrestricted
            ? query
            : query.Where(x => access.MerchantIds.Contains(x.MerchantId));

    private static InboundWebhookClaim Claim(InboundWebhookEvent entity) =>
        new(entity.Id, entity.Status, entity.PayloadFingerprint);

    private static InboundWebhookEventView Project(InboundWebhookEvent entity) => new(
        entity.Id, entity.PspConnectionId, entity.MerchantId, entity.PaymentSessionId, entity.OrderId,
        entity.PspCode, entity.ExternalEventId, entity.PayloadFingerprint, entity.SignatureValid,
        entity.Status.ToString().ToLowerInvariant(), entity.FailureCode, entity.ReceivedAt, entity.ProcessedAt);

    private static void Validate(InboundWebhookQuery query)
    {
        if (query.Page < 1 || query.Limit is < 1 or > 100)
            throw new ArgumentException("Page and limit are invalid.");
        if (query.Search?.Length > 128)
            throw new ArgumentException("Search must not exceed 128 characters.");
        if (query.From is { } from && query.To is { } to && from > to)
            throw new ArgumentException("From must not be after to.");
    }
}
