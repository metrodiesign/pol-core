namespace Producer.Domain;

/// <summary>Whether the producer registrant is a natural person or a registered company (REQ-7.1). Stored as int;
/// nullable on the profile until the form supplies it.</summary>
public enum PersonType
{
    Individual = 0,
    Juristic = 1,
}
