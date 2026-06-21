namespace SharedKernel;

/// <summary>
/// The cross-module money seam (PLAN decision #2). Stored as an integer count of the
/// currency's minor unit (e.g. THB satang) plus an ISO 4217 alpha-3 code — never a
/// float/decimal at a module boundary. Non-negative; addition is currency-checked and
/// overflow-checked. <c>default(Money)</c> is an invalid sentinel (Currency is null) and
/// throws on any arithmetic — always construct via <see cref="Of"/>.
/// </summary>
public readonly record struct Money
{
    public long MinorUnits { get; }

    /// <summary>ISO 4217 alpha-3 code, upper-invariant.</summary>
    public string Currency { get; }

    private Money(long minorUnits, string currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    public static Money Of(long minorUnits, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var code = currency.ToUpperInvariant();
        if (!Iso4217.IsSupported(code))
            throw new ArgumentOutOfRangeException(nameof(currency), code, "Unsupported ISO 4217 currency.");
        if (minorUnits < 0)
            throw new ArgumentOutOfRangeException(nameof(minorUnits), minorUnits, "Money cannot be negative.");
        return new Money(minorUnits, code);
    }

    public static Money Zero(string currency) => Of(0, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(MinorUnits + other.MinorUnits), Currency);
    }

    public bool SameCurrencyAs(Money other) =>
        string.Equals(Currency, other.Currency, StringComparison.Ordinal);

    private void EnsureSameCurrency(Money other)
    {
        if (Currency is null || other.Currency is null)
            throw new InvalidOperationException("Cannot operate on an uninitialised Money value.");
        if (!SameCurrencyAs(other))
            throw new InvalidOperationException($"Currency mismatch: {Currency} vs {other.Currency}.");
    }

    public override string ToString() => $"{MinorUnits} {Currency} (minor units)";
}
