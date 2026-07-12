namespace SharedKernel;

/// <summary>
/// The cross-module money seam (PLAN decision #2; decimal since the v5 restructure, rf1
/// REQ-6 — supersedes the earlier minor-units representation). Stored as an exact decimal
/// amount (scale &lt;= 4) plus an ISO 4217 alpha-3 code — never a float/double at a module
/// boundary. Non-negative; addition is currency-checked (decimal arithmetic overflow-checks
/// itself). <c>default(Money)</c> is an invalid sentinel (Currency is null) and throws on any
/// arithmetic — always construct via <see cref="Of"/>.
/// </summary>
public readonly record struct Money
{
    public decimal Amount { get; }

    /// <summary>ISO 4217 alpha-3 code, upper-invariant.</summary>
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var code = currency.ToUpperInvariant();
        if (!Iso4217.IsSupported(code))
            throw new ArgumentOutOfRangeException(nameof(currency), code, "Unsupported ISO 4217 currency.");
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Money cannot be negative.");
        if (amount != decimal.Round(amount, 4))
            throw new ArgumentException($"Money scale must be <= 4 decimal places: {amount}.", nameof(amount));
        return new Money(amount, code);
    }

    public static Money Zero(string currency) => Of(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
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

    public override string ToString() => $"{Amount:F4} {Currency}";
}
