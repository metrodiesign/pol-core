using Payments.Domain;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// Adapter for 2C2P (code string "2c2p"). Redirect-only: card payments go through 2C2P's hosted
/// payment page (the returned <c>RedirectUrl</c>), never card fields on our side.
/// <para>ponytail: inherits the deterministic stub behaviour from <see cref="StubPspAdapter"/>; no
/// real 2C2P HTTP yet. Upgrade path: implement the 2C2P PaymentToken + hosted redirect flow and the
/// JWT/HMAC webhook verify, keeping verbatim 2C2P field names.</para>
/// </summary>
public sealed class TwoCTwoPAdapter : StubPspAdapter
{
    public override PspCode Psp => PspCode.TwoCTwoP;
}
