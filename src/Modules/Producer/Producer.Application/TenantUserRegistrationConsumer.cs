using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Producer.Domain;

namespace Producer.Application;

/// <summary>
/// Admin-side consumer of <see cref="TenantUserRegistrationSubmitted"/> (REQ-20.4): records a "registration awaiting
/// approval" notice so the Admin side learns of a pending producer without a synchronous coupling. Runs on the
/// outbox dispatcher (pol_worker) under the event's sentinel-tenant SESSION_CONTEXT, so it touches ONLY the
/// control-plane <c>ProducerRegistrationNotices</c> table (granted to pol_worker) — never a tenant-scoped table,
/// which the sentinel context would FILTER/BLOCK and poison the message (S5). Idempotent on TenantUserId: a
/// redelivered event records nothing twice and never throws, so at-least-once delivery cannot poison the dispatcher.
/// </summary>
public sealed class TenantUserRegistrationConsumer : INotificationHandler<TenantUserRegistrationSubmitted>
{
    private readonly IProducerRegistrationNoticeWriter _notices;
    private readonly IClock _clock;

    public TenantUserRegistrationConsumer(IProducerRegistrationNoticeWriter notices, IClock clock)
    {
        _notices = notices;
        _clock = clock;
    }

    public async ValueTask Handle(TenantUserRegistrationSubmitted notification, CancellationToken cancellationToken)
    {
        // Idempotent fast path: a notice already recorded for this registration (a redelivery, or a correction
        // resubmission of the same user) is a no-op — one notice per pending producer.
        if (await _notices.ExistsAsync(notification.TenantUserId, cancellationToken).ConfigureAwait(false))
            return;

        _notices.Add(ProducerRegistrationNotice.For(
            notification.TenantUserId, notification.Subject, notification.Email, notification.DisplayName,
            notification.HostedDomain, notification.OccurredAt, _clock.UtcNow));

        // TrySave swallows a unique-violation (a concurrent redelivery that won the race) as an idempotent no-op,
        // so the dispatcher marks the message processed instead of retrying it to poison.
        await _notices.TrySaveAsync(cancellationToken).ConfigureAwait(false);
    }
}
