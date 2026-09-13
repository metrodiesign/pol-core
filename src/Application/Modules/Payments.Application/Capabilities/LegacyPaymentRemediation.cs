using SharedKernel;

namespace Payments.Application.Capabilities;

/// <summary>The outcome of a legacy Session remediation pass (merchant-psp-settings task 9, design steps
/// 5-9): how many version 0 rows were upgraded to the version 1 snapshot contract and how many stayed
/// version 0 because a single historical secret could not be proven (they keep blocking credential/
/// environment activation with <c>legacy_snapshot_blocked</c> until an operator resolves them).</summary>
public sealed record LegacySessionRemediationReport(int Upgraded, int Blocked);

/// <summary>
/// Offline/operator seam for the merchant-psp-settings cutover (design "Migration and cutover" steps 3, 5-9).
/// No HTTP route invokes it and it never runs automatically at boot — an operator runs it out-of-band after
/// the expand migration, exactly like <see cref="IPaymentCapabilityMigration"/>. It reads the retired global
/// PSP environment as an explicit argument (the <c>Psp:UseSandbox</c> config key is gone from the app) rather
/// than resolving it from options.
/// </summary>
public interface ILegacyPaymentRemediation
{
    /// <summary>
    /// Bootstraps <c>Merchant.PaymentEnvironment</c> from the retired global default for merchants that have
    /// never switched environment (design step 3). "Never switched" is <c>PaymentEnvironmentUpdatedAt ==
    /// CreatedAt</c> — the expand migration seeded that equality, and a real switch bumps
    /// <c>PaymentEnvironmentUpdatedAt</c> — so a merchant that already chose an environment is left untouched
    /// and merchants created after cutover keep the sandbox default. Returns the number of merchants updated.
    /// </summary>
    Task<int> BackfillMerchantEnvironmentsAsync(
        Guid actorId, PspEnvironment globalDefault, CancellationToken cancellationToken);

    /// <summary>
    /// Remediates legacy snapshot version 0 Sessions (design steps 5-9). A row with no external charge is
    /// re-routed with the current selector and upgraded to version 1 before it can be claimed (step 5). A row
    /// that carries an external charge is proven read-only: each non-discarded secret version of the session's
    /// connection is tried with a fetch-to-confirm at <paramref name="legacyEnvironment"/> (never confirming
    /// or marking paid); when EXACTLY ONE version's fetch confirms the charge (matching amount/currency when
    /// the PSP reports an amount) the row is upgraded to version 1 pinned to that version, otherwise it stays
    /// version 0 (step 6, 8, 9 — no blind backfill from the current active value).
    /// </summary>
    Task<LegacySessionRemediationReport> RemediateSessionsAsync(
        Guid actorId, PspEnvironment legacyEnvironment, CancellationToken cancellationToken);
}
