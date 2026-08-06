using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orders.Application;

namespace Persistence.MerchantRuntime.Orders.Items;

/// <summary>
/// EF Core implementation of <see cref="IAdminItemPolicyReader"/> — the admin cross-merchant READ
/// escape-hatch for the policy report (policy-reference-record REQ-4.2/4.4). Unlike
/// <see cref="AdminItemPolicyWriter"/> (T5), this never calls <c>SaveChanges</c>, so it needs no separate
/// DI-factory context/authorizer — the AMBIENT <see cref="MerchantRuntimeDbContext"/> is enough (mirrors
/// <c>ConnectionRepository.ListByTenantAsync</c>). Every read here explicitly bypasses the merchant query
/// filter (<see cref="PolicyReportSfs.BuildQuery"/> with <c>ignoreFilters: true</c>) — allowlisted in
/// <c>Architecture.Tests.BypassPrimitiveTests</c> — and emits ONE <see cref="DenialEvent"/> per call (pattern
/// <c>ConnectionRepository.ListByTenantAsync</c> — not per row).
/// </summary>
internal sealed class AdminItemPolicyReader : IAdminItemPolicyReader
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly ISecurityTelemetry _telemetry;
    private readonly ILogger<AdminItemPolicyReader> _logger;

    public AdminItemPolicyReader(MerchantRuntimeDbContext db, ISecurityTelemetry telemetry, ILogger<AdminItemPolicyReader> logger)
    {
        _db = db;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task<PagedResult<PolicyReportItem>> ListAsync(ListPolicyReportAdminQuery query, CancellationToken cancellationToken)
    {
        _telemetry.Emit(new DenialEvent(
            DenialCategory.AdminCrossMerchantAction, "admin", ActorId: null, TargetMerchant: query.MerchantId,
            nameof(PolicyReportItem), "ListPolicyReport",
            "Admin cross-merchant policy report crossed the merchant read floor via IgnoreQueryFilters().",
            CorrelationId.Current, DateTime.UtcNow));

        // Confine to the caller's accessible set (Super = unrestricted -> null = no confinement), then
        // intersect with the optional ?merchantId= narrowing (not part of the SFS whitelist). A Scoped admin
        // naming a merchant outside its accessible set gets the empty set here -> an empty page, never a leak.
        IReadOnlySet<Guid>? confineToMerchants = query switch
        {
            { IsUnrestrictedAdmin: true, MerchantId: { } mid } => new HashSet<Guid> { mid },
            { IsUnrestrictedAdmin: true, MerchantId: null } => null,
            { IsUnrestrictedAdmin: false, MerchantId: { } mid } =>
                query.AccessibleMerchantIds.Contains(mid) ? new HashSet<Guid> { mid } : new HashSet<Guid>(),
            _ => query.AccessibleMerchantIds,
        };

        var confined = await PlatformReadGuard.ReadAsync(ct => PolicyReportSfs
            .BuildQuery(_db, ignoreFilters: true, confineToMerchants)
            .ToListAsync(ct), cancellationToken);   // bounded to the accessible/requested merchant set — see PolicyReportSfs's "ponytail" note

        var filtered = confined.ApplyFilters(query.Filters, _logger).ToList();

        int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);
        var items = filtered
            .ApplySort(query.Sort, _logger)
            .Skip(skip)
            .Take(query.Limit)
            .Select(r => r.ToItem(includeMerchantId: true))
            .ToList();

        return new PagedResult<PolicyReportItem>(items, query.Page, query.Limit, filtered.Count);
    }
}
