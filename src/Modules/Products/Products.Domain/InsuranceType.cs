namespace Products.Domain;

/// <summary>Source-system family of an insurance document (VCentralPay SP guide §1) — derived from
/// <see cref="ProductGroup"/> (<see cref="Product.InsuranceType"/>), never stored.</summary>
public enum InsuranceType
{
    Motor = 0,
    NonMotor = 1,
}
