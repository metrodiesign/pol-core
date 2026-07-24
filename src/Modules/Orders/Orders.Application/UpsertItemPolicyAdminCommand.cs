using BuildingBlocks.Application;
using Mediator;
using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Application;

/// <summary>
/// Admin-plane cross-merchant write for one item's external insurance-reference record
/// (REQ-3.2-admin/3.3-admin) — the escape-hatch twin of <see cref="UpsertItemPolicyCommand"/>. Deliberately
/// does NOT implement <see cref="IMerchantScoped"/> (an admin request has no bound merchant —
/// <c>MerchantGuardBehavior</c> would reject it). The caller's accessible-merchant decision travels as plain
/// data (<see cref="IsUnrestrictedAdmin"/>/<see cref="AccessibleMerchantIds"/>) rather than a dependency on
/// Admins.Application's <c>IAdminScope</c>: no module's Application project in this repo references another
/// module's Application project (Orders.Application's own csproj only references BuildingBlocks/Contracts/its
/// own Domain), and the accessible-set decision itself is one line — not worth a cross-module dependency for.
/// </summary>
public sealed record UpsertItemPolicyAdminCommand(
    Guid OrderItemId, ItemPolicyInput Input, string ActorId, bool IsUnrestrictedAdmin,
    IReadOnlySet<Guid> AccessibleMerchantIds) : ICommand<UpsertItemPolicyAdminResult>;

public sealed record UpsertItemPolicyAdminResult(Guid PolicyId);

/// <summary>
/// Handles <see cref="UpsertItemPolicyAdminCommand"/>: loads the item CROSS-MERCHANT via the escape-hatch
/// (<see cref="IAdminItemPolicyWriter"/>), then 404s when the item does not exist OR its real merchant falls
/// outside the admin's accessible set (REQ-3.3-admin — same code path for both, no existence leak, mirror
/// <c>Api.Admins.HostWiring.AdminQuery</c>'s own accessible-set-then-404 shape). Otherwise loads-or-creates
/// the policy, applies the input (every REQ-3/REQ-2 invariant lives in <see cref="ItemPolicy.Apply"/> and
/// throws <see cref="ArgumentException"/> => 400), and writes exactly one <see cref="ItemPolicyAudit"/> row
/// per call (REQ-3.5, actor kind <see cref="ActorKind.Admin"/>) — the same shape
/// <see cref="UpsertItemPolicyHandler"/> (T4) uses for the merchant plane, copied rather than shared (no
/// common base exists and design.md does not ask for one).
/// </summary>
public sealed class UpsertItemPolicyAdminHandler
    : ICommandHandler<UpsertItemPolicyAdminCommand, UpsertItemPolicyAdminResult>
{
    private readonly IAdminItemPolicyWriter _writer;
    private readonly IClock _clock;

    public UpsertItemPolicyAdminHandler(IAdminItemPolicyWriter writer, IClock clock)
    {
        _writer = writer;
        _clock = clock;
    }

    public async ValueTask<UpsertItemPolicyAdminResult> Handle(
        UpsertItemPolicyAdminCommand command, CancellationToken cancellationToken)
    {
        var load = await _writer.LoadAsync(command.OrderItemId, cancellationToken).ConfigureAwait(false);
        var allowed = command.IsUnrestrictedAdmin || command.AccessibleMerchantIds.Contains(load.MerchantId);
        if (!load.ItemExists || !allowed)
            throw new NotFoundException($"Item {command.OrderItemId} was not found.");

        var nowUtc = _clock.UtcNow;
        var isNew = load.Policy is null;
        var policy = load.Policy ?? ItemPolicy.Create(Guid.NewGuid(), command.OrderItemId, load.MerchantId, nowUtc);

        var before = Snapshot(policy);
        policy.Apply(command.Input, nowUtc);

        if (isNew)
            _writer.Add(policy);

        _writer.AddAudit(ItemPolicyAudit.For(
            command.OrderItemId, load.MerchantId, command.ActorId, ActorKind.Admin,
            isNew ? AuditOperation.Created : AuditOperation.Updated,
            ChangeSummary(before, policy), CorrelationId.Current, nowUtc));

        await _writer.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new UpsertItemPolicyAdminResult(policy.Id);
    }

    private readonly record struct FieldSnapshot(
        InsuranceCategory? InsuranceCategory, ReferenceNumberType? ReferenceNumberType, string? ReferenceNumber,
        string? EndorsementNumber, string? RenewalReminderNumber, string? InsuredObjectReference,
        Money? NetPremium, Money? GrossPremium,
        PremiumRemittanceStatus PremiumRemittanceStatus, DateOnly? DeductedAt);

    private static FieldSnapshot Snapshot(ItemPolicy policy) => new(
        policy.InsuranceCategory, policy.ReferenceNumberType, policy.ReferenceNumber, policy.EndorsementNumber,
        policy.RenewalReminderNumber, policy.InsuredObjectReference, policy.NetPremium, policy.GrossPremium,
        policy.PremiumRemittanceStatus, policy.DeductedAt);

    // Field NAMES only — never values (ChangeSummary carries no PII/secret, REQ-4.6/design.md).
    private static string ChangeSummary(FieldSnapshot before, ItemPolicy after)
    {
        var changed = new List<string>();
        if (before.InsuranceCategory != after.InsuranceCategory) changed.Add(nameof(ItemPolicy.InsuranceCategory));
        if (before.ReferenceNumberType != after.ReferenceNumberType) changed.Add(nameof(ItemPolicy.ReferenceNumberType));
        if (before.ReferenceNumber != after.ReferenceNumber) changed.Add(nameof(ItemPolicy.ReferenceNumber));
        if (before.EndorsementNumber != after.EndorsementNumber) changed.Add(nameof(ItemPolicy.EndorsementNumber));
        if (before.RenewalReminderNumber != after.RenewalReminderNumber) changed.Add(nameof(ItemPolicy.RenewalReminderNumber));
        if (before.InsuredObjectReference != after.InsuredObjectReference) changed.Add(nameof(ItemPolicy.InsuredObjectReference));
        if (before.NetPremium != after.NetPremium) changed.Add(nameof(ItemPolicy.NetPremium));
        if (before.GrossPremium != after.GrossPremium) changed.Add(nameof(ItemPolicy.GrossPremium));
        if (before.PremiumRemittanceStatus != after.PremiumRemittanceStatus) changed.Add(nameof(ItemPolicy.PremiumRemittanceStatus));
        if (before.DeductedAt != after.DeductedAt) changed.Add(nameof(ItemPolicy.DeductedAt));
        return string.Join(",", changed);
    }
}
