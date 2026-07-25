using BuildingBlocks.Application;
using Mediator;
using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Application;

/// <summary>
/// Merchant-plane write for one item's external insurance-reference record (REQ-3.1/3.2/3.3/3.4) — load-or-
/// create then <see cref="ItemPolicy.Apply"/>, exactly as design.md's sequence diagram names it. Deliberately
/// does NOT gate on <c>Order.Status</c> (REQ-3.4 — writable on a Cancelled order too); the repository's own
/// query filter is the only merchant boundary (REQ-3.3).
/// </summary>
public sealed record UpsertItemPolicyCommand(Guid MerchantId, Guid OrderItemId, ItemPolicyInput Input, string ActorId)
    : ICommand<UpsertItemPolicyResult>, IMerchantScoped;

public sealed record UpsertItemPolicyResult(Guid PolicyId);

/// <summary>Handles <see cref="UpsertItemPolicyCommand"/>: 404s when the item doesn't exist under the bound
/// merchant (REQ-3.3 — checked BEFORE the load-or-create, so "unknown item" and "item with no policy yet"
/// never share a code path), otherwise loads-or-creates the policy, applies the input (every REQ-3/REQ-2
/// invariant lives in <see cref="ItemPolicy.Apply"/> and throws <see cref="ArgumentException"/> => 400), and
/// writes exactly one <see cref="ItemPolicyAudit"/> row per call (REQ-3.5) — always, even when nothing in the
/// diff actually changed.</summary>
public sealed class UpsertItemPolicyHandler : ICommandHandler<UpsertItemPolicyCommand, UpsertItemPolicyResult>
{
    private readonly IItemPolicyRepository _policies;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public UpsertItemPolicyHandler(IItemPolicyRepository policies, IUnitOfWork unitOfWork, IClock clock)
    {
        _policies = policies;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<UpsertItemPolicyResult> Handle(
        UpsertItemPolicyCommand command, CancellationToken cancellationToken)
    {
        var itemExists = await _policies.ItemExistsAsync(command.OrderItemId, cancellationToken).ConfigureAwait(false);
        if (!itemExists)
            throw new NotFoundException($"Item {command.OrderItemId} was not found.");

        var nowUtc = _clock.UtcNow;
        var existing = await _policies.GetPolicyByItemAsync(command.OrderItemId, cancellationToken).ConfigureAwait(false);
        var isNew = existing is null;
        var policy = existing ?? ItemPolicy.Create(Guid.NewGuid(), command.OrderItemId, command.MerchantId, nowUtc);

        var before = Snapshot(policy);
        policy.Apply(command.Input, nowUtc);

        if (isNew)
            _policies.Add(policy);

        _policies.AddAudit(ItemPolicyAudit.For(
            command.OrderItemId, command.MerchantId, command.ActorId, ActorKind.Merchant,
            isNew ? AuditOperation.Created : AuditOperation.Updated,
            ChangeSummary(before, policy), CorrelationId.Current, nowUtc));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new UpsertItemPolicyResult(policy.Id);
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
