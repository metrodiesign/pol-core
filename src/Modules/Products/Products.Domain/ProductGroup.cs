namespace Products.Domain;

/// <summary>Product group / source system of an insurance document (VCentralPay SP guide §2
/// <c>@ProductGroup</c>, §5.2 <c>SourceSystem</c>). Members are uppercase on purpose so the EF
/// enum-to-string conversion emits the exact wire values of the SP contract.</summary>
public enum ProductGroup
{
    CMI = 0,
    VMI = 1,
    FIRE = 2,
    MISC = 3,
}
