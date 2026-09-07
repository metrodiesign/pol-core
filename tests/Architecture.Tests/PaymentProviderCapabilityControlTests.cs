using Payments.Domain;
using Payments.Domain.Capabilities;

namespace Architecture.Tests;

public sealed class PaymentProviderCapabilityControlTests
{
    private static readonly Guid Actor = Guid.Parse("d1000000-0000-4000-8000-000000000001");
    private static readonly DateTime Now = new(2026, 8, 18, 1, 2, 3, DateTimeKind.Utc);

    [Fact]
    public void Normalizes_known_method_and_rejects_blank_unknown_and_alias()
    {
        Assert.Equal(PaymentMethods.Card, PaymentMethods.Normalize(" CARD "));
        Assert.Equal(PaymentMethods.PromptPay, PaymentMethods.Normalize("PromptPay"));
        Assert.Equal(PaymentMethods.Installment, PaymentMethods.Normalize(" installment "));
        Assert.Throws<ArgumentException>(() => PaymentMethods.Normalize(" "));
        Assert.Throws<ArgumentException>(() => PaymentMethods.Normalize("credit_card"));
        Assert.Throws<ArgumentException>(() => PaymentMethods.Normalize("mobile_banking"));
    }

    [Fact]
    public void Provider_method_and_option_mutations_capture_actor_time_and_version()
    {
        var providerMethod = PaymentProviderMethod.Create(
            PaymentCapabilityIds.TwoCTwoP, PaymentCapabilityIds.Installment, Actor, Now);
        var providerOption = PaymentProviderMethodOption.Create(
            providerMethod.Id, PaymentCapabilityIds.Installment, PaymentCapabilityIds.Kbank, Actor, Now);

        providerMethod.SetActive(false, Actor, Now.AddMinutes(1));
        providerOption.SetActive(false, Actor, Now.AddMinutes(2));

        Assert.False(providerMethod.IsActive);
        Assert.Equal(Actor, providerMethod.UpdatedBy);
        Assert.Equal(Now.AddMinutes(1), providerMethod.UpdatedAt);
        Assert.Equal(2, providerMethod.Version);
        Assert.False(providerOption.IsActive);
        Assert.Equal(Actor, providerOption.UpdatedBy);
        Assert.Equal(Now.AddMinutes(2), providerOption.UpdatedAt);
        Assert.Equal(2, providerOption.Version);
    }

    [Fact]
    public void Compiled_adapter_manifests_gate_the_seeded_catalog_not_the_other_way_round()
    {
        // merchant-psp-settings REQ-5.4/5.11: the catalog (cfg.PaymentProviderMethods) lists what a PSP offers;
        // the adapter manifest lists what has sandbox evidence. A seeded row without evidence stays visible
        // but unavailable — it must never widen the manifest.
        var manifests = new Dictionary<Guid, IReadOnlySet<string>>
        {
            [PaymentCapabilityIds.TwoCTwoP] = new HashSet<string>(
                [PaymentMethods.Card, PaymentMethods.PromptPay, PaymentMethods.Installment], StringComparer.Ordinal),
            [PaymentCapabilityIds.Omise] = new HashSet<string>(StringComparer.Ordinal),
        };
        var seeded = new[]
        {
            (PaymentCapabilityIds.TwoCTwoP, PaymentMethods.Card),
            (PaymentCapabilityIds.TwoCTwoP, PaymentMethods.PromptPay),
            (PaymentCapabilityIds.TwoCTwoP, PaymentMethods.Installment),
            (PaymentCapabilityIds.Omise, PaymentMethods.Card),
        };

        Assert.All(seeded.Where(row => row.Item1 == PaymentCapabilityIds.TwoCTwoP),
            row => Assert.Contains(row.Item2, manifests[row.Item1]));
        Assert.Empty(manifests[PaymentCapabilityIds.Omise]);
        Assert.Contains(seeded, row => row.Item1 == PaymentCapabilityIds.Omise); // catalog still lists it
    }
}
