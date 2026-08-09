namespace Merchants.Domain.Users;

/// <summary>Whether the registrant is a natural person or a registered company. Stored as a required int.</summary>
public enum IdentityType
{
    Individual = 1,
    Juristic = 2,
}
