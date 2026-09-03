namespace Admins.Domain.Users;

public enum MicrosoftWorkforceIdentityState
{
    BoundMicrosoft,
    BoundNonMicrosoft,
}

/// <summary>Pure final-state policy for persisted Admin external identity.</summary>
public static class MicrosoftWorkforceIdentityPolicy
{
    public static bool IsCanonicalObjectId(string? subject) =>
        subject is not null
        && Guid.TryParseExact(subject, "D", out var objectId)
        && objectId != Guid.Empty
        && string.Equals(subject, objectId.ToString("D"), StringComparison.Ordinal);

    public static bool TryClassifyFinal(
        string provider,
        Guid? tenantId,
        string? subject,
        Guid persistedTenantId,
        out MicrosoftWorkforceIdentityState state)
    {
        state = default;
        if (persistedTenantId == Guid.Empty)
            return false;

        if (string.Equals(provider, User.MicrosoftProvider, StringComparison.Ordinal))
        {
            if (tenantId != persistedTenantId || !IsCanonicalObjectId(subject))
                return false;

            state = MicrosoftWorkforceIdentityState.BoundMicrosoft;
            return true;
        }

        if (string.Equals(provider, User.MicrosoftProvider, StringComparison.OrdinalIgnoreCase)
            || tenantId is not null
            || subject is null)
        {
            return false;
        }

        state = MicrosoftWorkforceIdentityState.BoundNonMicrosoft;
        return true;
    }
}
