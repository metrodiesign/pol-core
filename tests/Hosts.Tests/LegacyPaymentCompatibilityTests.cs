extern alias ApiHost;
using Microsoft.AspNetCore.Http;
using Payments.Domain.Psp;

namespace Hosts.Tests;

/// <summary>
/// merchant-psp-settings AC-9.4: a create-session request that still carries the legacy <c>psp</c> field is
/// flagged deprecated AND recorded so the cutover team can watch the signal fall to zero before the field is
/// removed (design step 11-12). A request without the field touches neither.
/// </summary>
public sealed class LegacyPaymentCompatibilityTests
{
    [Fact]
    public void A_legacy_psp_field_sets_the_deprecation_header_and_records_telemetry()
    {
        var http = new DefaultHttpContext();
        var telemetry = new CountingTelemetry();

        ApiHost::Api.PaymentCompatibility.LegacyPaymentCompatibility.FlagLegacyPsp(
            http.Response, Code.TwoCTwoP, telemetry);

        Assert.Equal("true", http.Response.Headers["Deprecation"]);
        Assert.Equal(1, telemetry.Count);
    }

    [Fact]
    public void A_request_without_the_legacy_field_neither_flags_nor_records()
    {
        var http = new DefaultHttpContext();
        var telemetry = new CountingTelemetry();

        ApiHost::Api.PaymentCompatibility.LegacyPaymentCompatibility.FlagLegacyPsp(
            http.Response, null, telemetry);

        Assert.False(http.Response.Headers.ContainsKey("Deprecation"));
        Assert.Equal(0, telemetry.Count);
    }

    private sealed class CountingTelemetry : ApiHost::Api.PaymentCompatibility.ILegacyPaymentCompatibilityTelemetry
    {
        public int Count { get; private set; }
        public void LegacyPspFieldReceived() => Count++;
    }
}
