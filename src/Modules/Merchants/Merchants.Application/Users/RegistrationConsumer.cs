using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Merchants.Domain;
using Merchants.Domain.Users;

namespace Merchants.Application.Users;

/// <summary>
/// Admin-side consumer of <see cref="MerchantUserRegistrationSubmitted"/> (REQ-20.4): records a "registration awaiting
/// approval" notice so the Admin side learns of a pending merchant user without a synchronous coupling. Runs on the
/// outbox dispatcher (pol_worker) under the event's sentinel-merchant SESSION_CONTEXT, so it touches ONLY the
/// control-plane <c>RegistrationNotices</c> table (granted to pol_worker) — never a merchant-scoped table,
/// which the sentinel context would FILTER/BLOCK and poison the message (S5). Idempotent on MerchantUserId: a
/// redelivered event records nothing twice and never throws, so at-least-once delivery cannot poison the dispatcher.
/// </summary>
public sealed class RegistrationConsumer : INotificationHandler<MerchantUserRegistrationSubmitted>
{
    private readonly IRegistrationNoticeWriter _notices;
    private readonly IClock _clock;

    public RegistrationConsumer(IRegistrationNoticeWriter notices, IClock clock)
    {
        _notices = notices;
        _clock = clock;
    }

    public async ValueTask Handle(MerchantUserRegistrationSubmitted notification, CancellationToken cancellationToken)
    {
        // Idempotent fast path: a notice already recorded for this registration (a redelivery, or a correction
        // resubmission of the same user) is a no-op — one notice per pending merchant user.
        if (await _notices.ExistsAsync(notification.MerchantUserId, cancellationToken).ConfigureAwait(false))
            return;

        _notices.Add(RegistrationNotice.For(
            notification.MerchantUserId, notification.Subject, notification.Email, notification.DisplayName,
            notification.HostedDomain, notification.OccurredAt, _clock.UtcNow));

        // TrySave swallows a unique-violation (a concurrent redelivery that won the race) as an idempotent no-op,
        // so the dispatcher marks the message processed instead of retrying it to poison.
        await _notices.TrySaveAsync(cancellationToken).ConfigureAwait(false);
    }
}
