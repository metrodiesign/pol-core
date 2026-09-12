using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Application.Ports;

/// <summary>
/// The immutable routing decision the server makes for one payment attempt: which connection charges it,
/// under which vault secret version, in which endpoint family. Pinned onto the <c>Session</c> at creation
/// so a later settings change never moves the attempt (REQ-2.8-2.10). The client never supplies any of it.
/// </summary>
public sealed record PspRouteSelection(
    Guid PspConnectionId,
    Code Psp,
    Guid SecretVersionId,
    PspEnvironment Environment);

/// <summary>
/// The single server-side routing authority (REQ-6.8-6.18). Every audience — merchant API, legacy client
/// and admin — resolves its PSP through here; no other code path selects a connection. Local eligibility
/// only: enabled state, account AND merchant method policy (REQ-5.15), environment match and a credential
/// reference. It never probes the PSP live (REQ-6.16) and never uses health (REQ-6.15). Primary is tried
/// before fallback; there is no deployment default — an uncovered method is refused (REQ-6.18).
/// </summary>
public interface IPaymentRouteSelector
{
    Task<PspRouteSelection> SelectAsync(
        Guid merchantId, Guid orderId, string method, CancellationToken cancellationToken);
}
