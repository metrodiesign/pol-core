using BuildingBlocks.Application;
using Checkouts.Application;
using Checkouts.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Domain;
using SharedKernel;
using System.Security.Cryptography;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>
/// Anonymous checkout read boundary. It resolves by the keyed digest through a fresh scope and projects only
/// the fields needed by Checkout; raw token, replay ciphertext, provider/session data and internal metadata
/// never enter either projection.
/// </summary>
internal sealed class CustomerCheckoutReader(IServiceScopeFactory scopeFactory) : ICustomerCheckoutReader
{
    public async Task<CheckoutLinkSnapshot?> FindByHashAsync(
        byte[] tokenHash, CancellationToken cancellationToken)
    {
        if (tokenHash is null || tokenHash.Length != 32)
            return null;
        using var scope = scopeFactory.CreateScope();
        var db = ResolveDb(scope.ServiceProvider);
        var parameter = new SqlParameter("@p0", System.Data.SqlDbType.Binary, 32) { Value = tokenHash };
        var rows = await PlatformReadGuard.ReadAsync(ct => db.Database
            .SqlQueryRaw<CheckoutLinkRow>("""
                SELECT TOP 1
                    l.Id AS LinkId, l.MerchantId, l.OrderId, l.TokenHash,
                    l.Status AS LinkStatus,
                    l.CreatedAt AS LinkCreatedAt, l.ExpiresAt AS LinkExpiresAt,
                    l.RevokedAt AS LinkRevokedAt, l.Version AS LinkVersion,
                    o.Status AS OrderStatus, o.PaymentStatus, o.Version AS OrderVersion
                FROM checkout.PaymentLinks AS l
                INNER JOIN shop.Orders AS o
                    ON o.Id = l.OrderId AND o.MerchantId = l.MerchantId
                WHERE l.TokenHash = @p0;
                """, parameter)
            .ToListAsync(ct), cancellationToken).ConfigureAwait(false);
        var row = rows.FirstOrDefault();
        return row is null || row.TokenHash is null
            || !CryptographicOperations.FixedTimeEquals(row.TokenHash, tokenHash)
            ? null
            : new CheckoutLinkSnapshot(
                row.LinkId, row.MerchantId, row.OrderId, (PaymentLinkStatus)row.LinkStatus,
                row.LinkCreatedAt, row.LinkExpiresAt, row.LinkRevokedAt, row.LinkVersion,
                (OrderStatus)row.OrderStatus, (PaymentStatus)row.PaymentStatus, row.OrderVersion);
    }

    public async Task<CheckoutOrderSnapshot?> GetSummaryAsync(
        Guid orderId, Guid linkId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || linkId == Guid.Empty)
            return null;
        using var scope = scopeFactory.CreateScope();
        var db = ResolveDb(scope.ServiceProvider);
        var header = await PlatformReadGuard.ReadAsync(ct => db.Database
            .SqlQueryRaw<CheckoutOrderRow>("""
                SELECT TOP 1
                    o.Id AS OrderId, l.Id AS LinkId, o.MerchantId AS MerchantId,
                    o.Version AS OrderVersion,
                    l.Status AS LinkStatus, l.ExpiresAt AS LinkExpiresAt,
                    o.OrderNo, m.Name AS MerchantName, o.Status AS OrderStatus,
                    o.PaymentStatus, o.AmountAmount AS TotalAmount,
                    o.AmountCurrency AS TotalCurrency
                FROM checkout.PaymentLinks AS l
                INNER JOIN shop.Orders AS o
                    ON o.Id = l.OrderId AND o.MerchantId = l.MerchantId
                LEFT JOIN merch.Merchants AS m ON m.Id = o.MerchantId
                WHERE o.Id = @p0 AND l.Id = @p1;
                """, new SqlParameter("@p0", orderId), new SqlParameter("@p1", linkId))
            .ToListAsync(ct), cancellationToken).ConfigureAwait(false);
        var row = header.FirstOrDefault();
        if (row is null)
            return null;

        var lines = await PlatformReadGuard.ReadAsync(ct => db.Database
            .SqlQueryRaw<CheckoutSummaryLineRow>("""
                SELECT ProductCode, VariantCode, VariantName, Quantity,
                       LineAmount, LineCurrency
                FROM shop.OrderItems
                WHERE OrderId = @p0 AND MerchantId = @p2
                ORDER BY Id;
                """, new SqlParameter("@p0", orderId), new SqlParameter("@p2", row.MerchantId))
            .ToListAsync(ct), cancellationToken).ConfigureAwait(false);

        return new CheckoutOrderSnapshot(
            row.OrderId,
            row.LinkId,
            row.OrderVersion,
            (PaymentLinkStatus)row.LinkStatus,
            row.LinkExpiresAt,
            row.OrderNo,
            row.MerchantName,
            (OrderStatus)row.OrderStatus,
            (PaymentStatus)row.PaymentStatus,
            Money.Of(row.TotalAmount, row.TotalCurrency),
            lines.Select(x => new CheckoutSummaryLine(
                x.ProductCode, x.VariantCode, x.VariantName, x.Quantity,
                Money.Of(x.LineAmount, x.LineCurrency))).ToList(),
            row.MerchantId);
    }

    private static CommerceDbContext ResolveDb(IServiceProvider services) =>
        services.GetRequiredService<CommerceDbContext>();

    private sealed class CheckoutLinkRow
    {
        public Guid LinkId { get; set; }
        public Guid MerchantId { get; set; }
        public Guid OrderId { get; set; }
        public byte[]? TokenHash { get; set; }
        public int LinkStatus { get; set; }
        public DateTime LinkCreatedAt { get; set; }
        public DateTime LinkExpiresAt { get; set; }
        public DateTime? LinkRevokedAt { get; set; }
        public long LinkVersion { get; set; }
        public int OrderStatus { get; set; }
        public int PaymentStatus { get; set; }
        public long OrderVersion { get; set; }
    }

    private sealed class CheckoutOrderRow
    {
        public Guid OrderId { get; set; }
        public Guid LinkId { get; set; }
        public Guid MerchantId { get; set; }
        public long OrderVersion { get; set; }
        public int LinkStatus { get; set; }
        public DateTime LinkExpiresAt { get; set; }
        public string OrderNo { get; set; } = default!;
        public string? MerchantName { get; set; }
        public int OrderStatus { get; set; }
        public int PaymentStatus { get; set; }
        public decimal TotalAmount { get; set; }
        public string TotalCurrency { get; set; } = default!;
    }

    private sealed class CheckoutSummaryLineRow
    {
        public string ProductCode { get; set; } = default!;
        public string VariantCode { get; set; } = default!;
        public string? VariantName { get; set; }
        public int Quantity { get; set; }
        public decimal LineAmount { get; set; }
        public string LineCurrency { get; set; } = default!;
    }
}
