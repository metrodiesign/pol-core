namespace Merchants.Domain;

/// <summary>Whether the registrant is a natural person or a registered company. Stored as int;
/// nullable on the profile until the form supplies it.</summary>
public enum PersonType
{
    Individual = 0,
    Juristic = 1,
}
