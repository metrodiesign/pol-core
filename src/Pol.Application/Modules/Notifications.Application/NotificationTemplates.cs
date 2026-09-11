namespace Notifications.Application;

public sealed record NotificationTemplateDefinition(
    string EventType,
    string Channel,
    string Version,
    string Locale,
    string Subject,
    string Content);

/// <summary>Release-owned templates. Editing a release means adding a new version.</summary>
public static class NotificationTemplates
{
    public static NotificationTemplateDefinition For(string eventType, string channel) =>
        (eventType, channel) switch
        {
            ("AgentRegistrationDecidedV1", "email") => new(
                eventType, channel, "agent-registration-result.v1", "th-TH",
                "ผลการสมัครตัวแทน",
                "ผลการสมัครตัวแทนของคุณถูกบันทึกแล้ว"),
            ("AgentRegistrationDecidedV1", "sms") => new(
                eventType, channel, "agent-registration-result.v1", "th-TH",
                "ผลการสมัครตัวแทน",
                "ผลการสมัครตัวแทนของคุณถูกบันทึกแล้ว"),
            ("payments.transaction-succeeded.v1", "email") => new(
                eventType, channel, "payment-succeeded.v1", "th-TH",
                "ชำระเงินสำเร็จ",
                "ระบบยืนยันการชำระเงินของคุณแล้ว"),
            ("payments.transaction-succeeded.v1", "sms") => new(
                eventType, channel, "payment-succeeded.v1", "th-TH",
                "ชำระเงินสำเร็จ",
                "ระบบยืนยันการชำระเงินของคุณแล้ว"),
            ("payments.transaction-succeeded.v1", "business_webhook") => new(
                eventType, channel, "payment-succeeded.v1", "th-TH",
                string.Empty,
                "signed-event-payload"),
            _ => throw new ArgumentException("Notification event is not supported.", nameof(eventType)),
        };
}
