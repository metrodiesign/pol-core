using Payments.Domain;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// Adapter for Omise (code string "omise"). Redirect-only. IMPORTANT: Omise PromptPay MUST be created
/// via Payment Links+ (a hosted <c>transaction_url</c> we redirect to) — NEVER the source+charge flow,
/// which returns an offline display QR (non-redirect = forbidden under PCI SAQ A / redirect-only).
/// <para>ponytail: inherits the deterministic stub behaviour from <see cref="StubPspAdapter"/>; no
/// real Omise HTTP yet. Upgrade path: implement Omise charge-create for "card" and Payment Links+ for
/// "promptpay" (hosted transaction_url), plus the Omise webhook signature verify and charge retrieve,
/// keeping verbatim Omise field names. Do NOT add a source+charge PromptPay path.</para>
/// </summary>
public sealed class OmiseAdapter : StubPspAdapter
{
    public override PspCode Psp => PspCode.Omise;
}
