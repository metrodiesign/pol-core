using Notifications.Application;
using Notifications.Domain;

namespace Notifications.Tests;

[Trait("Capability", "Notifications")]
public sealed class NotificationDomainTests
{
    private static readonly Guid MerchantId = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private static readonly DateTime Now = new(2026, 9, 10, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-9.1")]
    [Trait("Requirement", "REQ-9.8")]
    public void One_source_event_fans_out_to_distinct_email_and_sms_deliveries()
    {
        var source = Guid.NewGuid();
        var notification = Notification.Create(source, MerchantId, "AgentRegistrationDecidedV1", "{}", Now);
        var email = TemplateVersion.Release("AgentRegistrationDecidedV1", "email", "v1", "th-TH", "subject", "body", Now);
        var sms = TemplateVersion.Release("AgentRegistrationDecidedV1", "sms", "v1", "th-TH", "subject", "body", Now);

        var emailDelivery = Delivery.Create(notification.Id, source, MerchantId, "email", "agent@example.com", email, "{}", Now);
        var smsDelivery = Delivery.Create(notification.Id, source, MerchantId, "sms", "+66800000000", sms, "{}", Now);

        Assert.Equal(DeliveryStatus.Pending, emailDelivery.Status);
        Assert.Equal(DeliveryStatus.Pending, smsDelivery.Status);
        Assert.NotEqual(emailDelivery.RecipientFingerprint, smsDelivery.RecipientFingerprint);
    }

    [Fact]
    [Trait("Requirement", "REQ-9.3")]
    [Trait("Requirement", "REQ-9.10")]
    public void Delivery_keeps_template_and_recipient_snapshots_when_a_future_release_is_created()
    {
        var notification = Notification.Create(Guid.NewGuid(), MerchantId, "payments.transaction-succeeded.v1", "{}", Now);
        var first = TemplateVersion.Release("payments.transaction-succeeded.v1", "email", "v1", "th-TH", "old", "old body", Now);
        var delivery = Delivery.Create(notification.Id, notification.SourceEventId, MerchantId, "email",
            "buyer@example.com", first, "{}", Now);

        _ = TemplateVersion.Release("payments.transaction-succeeded.v1", "email", "v2", "th-TH", "new", "new body", Now.AddDays(1));

        Assert.Equal("buyer@example.com", delivery.RecipientSnapshot);
        Assert.Equal("v1", delivery.TemplateVersion);
        Assert.Equal("old body", delivery.TemplateContentSnapshot);
    }

    [Fact]
    [Trait("Requirement", "REQ-9.4")]
    [Trait("Requirement", "REQ-9.5")]
    public void Accepted_is_distinct_from_delivered_and_unknown_can_be_retried()
    {
        var delivery = NewDelivery();
        delivery.Claim("worker", Now, Now.AddMinutes(1));
        delivery.MarkAccepted("provider-1", Now.AddSeconds(1));
        Assert.Equal(DeliveryStatus.Accepted, delivery.Status);

        delivery.MarkDelivered("provider-1", Now.AddSeconds(2));
        Assert.Equal(DeliveryStatus.Delivered, delivery.Status);

        var unknown = NewDelivery();
        unknown.Claim("worker", Now, Now.AddMinutes(1));
        unknown.MarkUnknown("provider_timeout", Now.AddSeconds(1), Now.AddMinutes(1));
        Assert.Equal(DeliveryStatus.Unknown, unknown.Status);
        unknown.Claim("worker", Now.AddMinutes(1), Now.AddMinutes(2));
        Assert.Equal(2, unknown.AttemptCount);
    }

    [Fact]
    [Trait("Requirement", "REQ-9.2")]
    [Trait("Requirement", "REQ-9.5")]
    public void Failed_attempts_follow_the_contract_schedule_then_manual_queue()
    {
        var delivery = NewDelivery();
        Assert.Equal(TimeSpan.FromMinutes(1), Delivery.RetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(5), Delivery.RetryDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(15), Delivery.RetryDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(60), Delivery.RetryDelay(4));
        Assert.Equal(TimeSpan.FromMinutes(240), Delivery.RetryDelay(5));
        Assert.Null(Delivery.RetryDelay(6));

        for (var attempt = 1; attempt <= Delivery.MaxAttempts; attempt++)
        {
            var attemptAt = attempt == 1 ? Now : delivery.NextAttemptAt;
            delivery.Claim("worker", attemptAt, attemptAt.AddMinutes(1));
            delivery.MarkFailed("smtp_rejected", attemptAt.AddMinutes(1),
                Delivery.RetryDelay(attempt) is { } delay ? attemptAt.AddMinutes(1) + delay : null);
            if (attempt < Delivery.MaxAttempts)
                Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        }

        Assert.Equal(DeliveryStatus.ManualQueue, delivery.Status);
    }

    [Fact]
    [Trait("Requirement", "REQ-9.1")]
    public void Sms_can_be_marked_blocked_without_claiming_a_provider_attempt()
    {
        var delivery = NewDelivery("sms", "+66800000000");
        delivery.BlockNotConfigured("sms_not_configured", Now);

        Assert.Equal(DeliveryStatus.BlockedNotConfigured, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
    }

    private static Delivery NewDelivery(string channel = "email", string recipient = "buyer@example.com")
    {
        var source = Guid.NewGuid();
        var notification = Notification.Create(source, MerchantId, "payments.transaction-succeeded.v1", "{}", Now);
        var template = TemplateVersion.Release("payments.transaction-succeeded.v1", channel, "v1", "th-TH", "subject", "body", Now);
        return Delivery.Create(notification.Id, source, MerchantId, channel, recipient, template, "{}", Now);
    }
}
