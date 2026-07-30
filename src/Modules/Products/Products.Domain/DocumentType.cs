namespace Products.Domain;

/// <summary>Kind of insurance document (VCentralPay SP guide §2 <c>@DocumentType</c>). No <c>ALL</c>
/// member — a stored document always has a concrete type; filters use a nullable value (null = ALL).
/// Members are uppercase on purpose so the EF enum-to-string conversion emits the exact wire values.</summary>
public enum DocumentType
{
    APPLICATION = 0,
    POLICY = 1,
    RENEWAL = 2,
    ENDORSEMENT = 3,
}
