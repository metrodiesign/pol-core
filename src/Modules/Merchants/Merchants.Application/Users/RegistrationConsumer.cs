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
/// which the sentinel context would FILTER/BLOCK and poison the message (S5). Idempotent on UserId: a
/// redelivered event records nothing twice and never throws, so at-least-once delivery cannot poison the dispatcher.
/// </summary>
public sealed class RegistrationConsumer : INotificationHandler<MerchantUserRegistrationSubmitted>
{
    private readonly IRegistrationNoticeWriter _notices;
    private readonly IAccountResolver _accounts;
    private readonly IClock _clock;

    public RegistrationConsumer(IRegistrationNoticeWriter notices, IAccountResolver accounts, IClock clock)
    {
        _notices = notices;
        _accounts = accounts;
        _clock = clock;
    }

    public async ValueTask Handle(MerchantUserRegistrationSubmitted notification, CancellationToken cancellationToken)
    {
        // Idempotent fast path: a notice already recorded for this registration (a redelivery, or a correction
        // resubmission of the same user) is a no-op — one notice per pending merchant user.
        if (await _notices.ExistsAsync(notification.UserId, cancellationToken).ConfigureAwait(false))
            return;

        var account = await _accounts.FindByIdAsync(notification.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Registration account was not found for its outbox event.");

        _notices.Add(RegistrationNotice.For(
            notification.UserId, account.Subject, account.Email,
            account.DisplayName ?? throw new InvalidOperationException("Registration account has no display name."),
            hostedDomain: null, notification.OccurredAt, _clock.UtcNow));

        // TrySave swallows a unique-violation (a concurrent redelivery that won the race) as an idempotent no-op,
        // so the dispatcher marks the message processed instead of retrying it to poison.
        await _notices.TrySaveAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Applies KYC object lifecycle requests emitted atomically with the user-key update. Store operations are
/// idempotent, so outbox replay and a crash between promote/delete are safe.
/// </summary>
public sealed class KycPhotoLifecycleConsumer : INotificationHandler<KycPhotoLifecycleRequested>
{
    private readonly IPhotoStore _photos;

    public KycPhotoLifecycleConsumer(IPhotoStore photos) => _photos = photos;

    public async ValueTask Handle(KycPhotoLifecycleRequested notification, CancellationToken cancellationToken)
    {
        try
        {
            await _photos.CommitAsync(notification.NewObjectKey, cancellationToken).ConfigureAwait(false);

            if (notification.OldObjectKey is not null &&
                !string.Equals(notification.OldObjectKey, notification.NewObjectKey, StringComparison.Ordinal))
            {
                await _photos.DeleteAsync(notification.OldObjectKey, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Dispatcher persists and logs exception messages. Never let provider errors containing object keys
            // or physical paths cross this privacy boundary.
            throw new InvalidOperationException("KYC photo lifecycle operation failed.");
        }
    }
}
