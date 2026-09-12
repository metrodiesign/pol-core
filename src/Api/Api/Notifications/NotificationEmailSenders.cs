using System.Net;
using System.Net.Mail;
using Api.Merchants;
using Microsoft.Extensions.Options;
using Notifications.Application;

namespace Api.Notifications;

internal sealed class CaptureNotificationEmailSender : IEmailSenderPort
{
    public Task<DeliveryProviderResult> SendAsync(
        EmailDeliveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new DeliveryProviderResult(
            DeliveryProviderOutcome.Accepted,
            $"capture-{request.DeliveryId:N}"));
}

internal sealed class SmtpNotificationEmailSender(IOptions<UserInvitationOptions> options) : IEmailSenderPort
{
    public async Task<DeliveryProviderResult> SendAsync(
        EmailDeliveryRequest request, CancellationToken cancellationToken)
    {
        var smtp = options.Value.Smtp;
        var password = (await File.ReadAllTextAsync(smtp.PasswordFile, cancellationToken)).Trim();
        if (password.Length == 0)
            throw new InvalidOperationException("Invitation SMTP password file is empty.");

        using var message = new MailMessage(smtp.FromAddress, request.Recipient)
        {
            Subject = request.Subject,
            Body = request.Body,
            IsBodyHtml = false,
        };
        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = smtp.EnableSsl,
            Credentials = new NetworkCredential(smtp.Username, password),
        };
        await client.SendMailAsync(message, cancellationToken);
        return new DeliveryProviderResult(
            DeliveryProviderOutcome.Accepted,
            $"smtp-{request.DeliveryId:N}");
    }
}
