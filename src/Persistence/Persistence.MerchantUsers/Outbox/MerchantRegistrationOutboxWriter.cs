using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Mediator;
using Merchants.Application.Users;

namespace Persistence.MerchantUsers.Outbox;

/// <summary>
/// The sentinel <c>MerchantId</c> a still-pending (no real merchant yet) registration's outbox row carries —
/// the SAME value as the legacy <c>Merchants.Infrastructure.Persistence.Users.RegistrationOutbox.SentinelMerchantId</c>,
/// and the exact literal the <c>RlsTeardownAndOnePrincipal</c> migration already moved every existing
/// sentinel-marked row into <c>merch.UserOutbox</c> under (that migration's own comment: "task 8 sequencing
/// ... until 8.5 lands, submitting a new registration against a migrated DB fails on this constraint").
/// <para>
/// NOT removable, despite task 8.5.2's original brief asking for the sentinel to be dropped entirely:
/// <see cref="MerchantUserOutbox.MerchantId"/> is a non-nullable <see cref="Guid"/> (unlike <c>merch.Users
/// .MerchantId</c>, which is genuinely nullable) — <c>GuardedRuntimeDbContext.GuardTenantKey</c> rejects
/// <see cref="Guid.Empty"/> unconditionally, before the write authorizer is ever consulted, for ANY
/// non-nullable tenant-keyed property. A merchant-less row therefore needs a real, non-empty placeholder id,
/// exactly like before — this is a hard write-floor constraint, not a stylistic choice.
/// </para>
/// <para>
/// Public (not internal): <c>Api/Persistence/WriteAuthorizers.cs</c> needs this exact value for
/// <c>MerchantRequestWriteAuthorizer</c>'s sentinel carve-out (see below) — a plain marker GUID, not a
/// security boundary, so widening it is safe unlike the DbContext types this assembly otherwise keeps
/// internal.
/// </para>
/// </summary>
public static class MerchantRegistrationOutboxSentinel
{
    public static readonly Guid MerchantId = new("f0f0f0f0-0000-4000-8000-00000000ad17");
}

/// <summary>
/// rls-to-query-filter task 8.5.2 rewire of <c>Merchants.Infrastructure</c>'s <c>RegistrationOutboxWriter</c>
/// onto <see cref="MerchantUserDbContext.UserOutbox"/> (its real home, task 5) instead of the shared
/// <c>PolDbContext.OutboxMessages</c>. Same sentinel mechanism as before (see
/// <see cref="MerchantRegistrationOutboxSentinel"/>) — only the destination table/CLR type changed.
/// <para>
/// <c>Api/Persistence/WriteAuthorizers.cs</c>'s <c>MerchantRequestWriteAuthorizer</c> carries an explicit
/// carve-out for <see cref="MerchantRegistrationOutboxSentinel.MerchantId"/> (task 8.5.7) so this anonymous,
/// unbound-actor insert is allowed — the sentinel is the only non-Empty <c>targetMerchant</c> value that
/// capability accepts without a matching bound actor.
/// </para>
/// </summary>
internal sealed class MerchantRegistrationOutboxWriter : IRegistrationOutboxWriter
{
    private readonly MerchantUserDbContext _db;
    private readonly IClock _clock;

    public MerchantRegistrationOutboxWriter(MerchantUserDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public void Enqueue(INotification notification)
    {
        var serialized = MerchantUserOutboxEventRegistry.Serialize(notification);
        _db.UserOutbox.Add(MerchantUserOutbox.Create(
            Guid.CreateVersion7(), MerchantRegistrationOutboxSentinel.MerchantId,
            serialized.Type, serialized.Payload, _clock.UtcNow));
    }
}
