using Microsoft.AspNetCore.Http;
using Payments.Domain.Psp;

namespace Api.PaymentCompatibility;

/// <summary>
/// Deprecation telemetry for the payment-API compatibility window (merchant-psp-settings AC-9.4, design step
/// 4/11). The contract still accepts the legacy <c>psp</c> field but never routes on it; this records that a
/// caller still sent it so the cutover team can watch the signal fall to zero before removing the field
/// (design step 12). It is a plain observability seam — never business state.
/// </summary>
internal interface ILegacyPaymentCompatibilityTelemetry
{
    void LegacyPspFieldReceived();
}

/// <summary>The registered <see cref="ILegacyPaymentCompatibilityTelemetry"/>: a structured log the cutover
/// team can dashboard/alert on. No metrics pipeline exists in this host, so the log IS the signal.</summary>
internal sealed class LoggingLegacyPaymentCompatibilityTelemetry(
    ILogger<LoggingLegacyPaymentCompatibilityTelemetry> logger) : ILegacyPaymentCompatibilityTelemetry
{
    public void LegacyPspFieldReceived() =>
        logger.LogInformation(
            "A create-session caller sent the deprecated legacy 'psp' field (ignored for routing).");
}

internal static class LegacyPaymentCompatibility
{
    /// <summary>
    /// Flags a create-session request that still carries the legacy <c>psp</c> field: sets the
    /// <c>Deprecation: true</c> header (AC-4.7) and records the deprecation telemetry (AC-9.4). A request
    /// without the field touches neither. The value is never used to route.
    /// </summary>
    public static void FlagLegacyPsp(
        HttpResponse response, Code? psp, ILegacyPaymentCompatibilityTelemetry telemetry)
    {
        if (psp is null)
            return;
        response.Headers["Deprecation"] = "true";
        telemetry.LegacyPspFieldReceived();
    }
}
