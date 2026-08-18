using Payments.Domain.Capabilities;

namespace Payments.Application.Capabilities;

public sealed record PaymentCapabilityMigrationReport(
    PaymentAuthorizationMode Mode,
    DateTime? CutoffAt,
    int AccountMethods,
    int MerchantMethods,
    int UserMethods,
    int OrdersWithInitiatingContext,
    int UnresolvedConflicts);

public sealed class PaymentAuthorizationCutoverBlockedException(string message)
    : InvalidOperationException(message);

/// <summary>
/// Offline/operator seam for additive backfill, atomic authorization cutover and normalized-aware rollback.
/// No HTTP route invokes it and implementation never runs a production migration automatically.
/// </summary>
public interface IPaymentCapabilityMigration
{
    Task<PaymentCapabilityMigrationReport> BackfillAsync(
        Guid actorId,
        CancellationToken cancellationToken);

    Task<PaymentCapabilityMigrationReport> CutoverAsync(
        Guid actorId,
        bool oldInstancesDrained,
        CancellationToken cancellationToken);

    Task<PaymentAuthorizationMode> PrepareRollbackAsync(
        Guid actorId,
        bool normalizedAwareBinaryAvailable,
        CancellationToken cancellationToken);
}
