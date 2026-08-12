using System.Net;
using BuildingBlocks.Application;
using Notifications.Domain;
using Persistence.ControlPlane.Notifications;

namespace Architecture.Tests;

public sealed class DeliverySecurityTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    public void Webhook_destination_rejects_non_public_addresses(string value) =>
        Assert.True(SafeDestinationValidator.IsUnsafe(IPAddress.Parse(value)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Webhook_destination_accepts_public_addresses(string value) =>
        Assert.False(SafeDestinationValidator.IsUnsafe(IPAddress.Parse(value)));

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("https://user:pass@example.com/hook")]
    [InlineData("https://example.com/hook#fragment")]
    [InlineData("https://example.com:8443/hook")]
    public async Task Webhook_destination_rejects_unsafe_url_shape_before_delivery(string value)
    {
        var exception = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            new SafeDestinationValidator().ResolveAsync(value, CancellationToken.None));

        Assert.Equal("unsafe_destination", exception.Code);
    }

    [Fact]
    public void Webhook_replay_requires_a_completed_failed_delivery()
    {
        var now = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc);
        var delivery = WebhookDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "payment.failed", "txn-1", "{}", now);
        Assert.Throws<InvalidOperationException>(() => WebhookDelivery.Replay(delivery, "replay-1", now));

        delivery.Claim("worker-1", now, now.AddSeconds(30));
        delivery.Finish(delivered: false, latencyMs: 12, failureCode: "http_4xx", now, retryAt: null);
        var replay = WebhookDelivery.Replay(delivery, "replay-1", now.AddMinutes(1));

        Assert.Equal(DeliveryStatus.Pending, replay.Status);
        Assert.Equal(delivery.Id, replay.OriginalDeliveryId);
        Assert.Equal("replay-1", replay.ReplayKey);
    }
}
