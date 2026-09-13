using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.DataProtection;
using Notifications.Application;
using Persistence.MerchantRuntime.Notifications;

namespace Notifications.Tests;

[Trait("Capability", "Notifications")]
public sealed class NotificationSecurityTests
{
    [Fact]
    [Trait("Requirement", "REQ-9.6")]
    [Trait("Requirement", "REQ-9.7")]
    public async Task Unsafe_destination_stops_before_any_http_request()
    {
        var validator = new RejectingDestinationValidator();
        var sender = new SignedBusinessWebhookSender(
            validator, new EphemeralDataProtectionProvider());

        var result = await sender.SendAsync(new BusinessWebhookDeliveryRequest(
            Guid.NewGuid(), "https://localhost/hook", "protected-secret", "{}"), default);

        Assert.Equal(DeliveryProviderOutcome.Failed, result.Outcome);
        Assert.Equal("unsafe_destination", result.FailureCode);
        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    [Trait("Requirement", "REQ-9.6")]
    [Trait("Requirement", "REQ-9.7")]
    public async Task Redirect_response_is_recorded_without_following_the_location_and_signature_is_stable()
    {
        var destination = new CapturingDestinationValidator();
        var protection = new EphemeralDataProtectionProvider();
        var secret = "task7-secret";
        var protectedSecret = protection.CreateProtector("pol-core/delivery-secret/v1").Protect(secret);
        var http = new CapturingHttpHandler(() => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://127.0.0.1/private") },
        });
        var sender = new SignedBusinessWebhookSender(destination, protection, () => http);
        var deliveryId = Guid.NewGuid();
        const string payload = "{\"id\":1}";

        var result = await sender.SendAsync(new BusinessWebhookDeliveryRequest(
            deliveryId, "https://business.example/hook", protectedSecret, payload), default);

        Assert.Equal(DeliveryProviderOutcome.Failed, result.Outcome);
        Assert.Equal("redirect_rejected", result.FailureCode);
        Assert.Equal(1, http.Calls);
        Assert.Equal("https://business.example/hook", http.Request!.RequestUri!.ToString());
        Assert.Equal(payload, http.Body);
        var timestamp = http.Request.Headers.GetValues("X-POL-Timestamp").Single();
        var expected = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{deliveryId:D}.{payload}"))).ToLowerInvariant();
        Assert.Equal($"sha256={expected}", http.Request.Headers.GetValues("X-POL-Signature").Single());
        Assert.Equal(1, destination.Calls);
    }

    [Fact]
    [Trait("Requirement", "REQ-9.7")]
    public void Pinned_handler_disables_redirect_proxy_and_cookies()
    {
        using var handler = SignedBusinessWebhookSender.CreatePinnedHandler(IPAddress.Parse("8.8.8.8"));

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
    }

    private sealed class RejectingDestinationValidator : ISafeDestinationValidator
    {
        public int Calls { get; private set; }

        public Task<ValidatedDestination> ResolveAsync(string url, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidRequestException("unsafe", "unsafe_destination");
        }
    }

    private sealed class CapturingDestinationValidator : ISafeDestinationValidator
    {
        public int Calls { get; private set; }

        public Task<ValidatedDestination> ResolveAsync(string url, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ValidatedDestination(
                new Uri(url), IPAddress.Parse("8.8.8.8")));
        }
    }

    private sealed class CapturingHttpHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory();
        }
    }
}
