namespace Admins.Application.Users;

/// <summary>The four org-profile reference dimensions an admin account carries as FKs.</summary>
public enum ProfileField { Position, Office, Level, Division }

/// <summary>A resolved profile reference embedded in an admin's detail (id + code + display name).</summary>
public sealed record ProfileRef(Guid Id, string Code, string Name);

/// <summary>
/// Admins' own port over the four profile reference lists (masterdata-split design.md) — existence/lookup is
/// a caller need, not a use case of the reference modules, so it lives here. Enum-keyed rather than generic
/// so Admins.Application references no reference-data module at all (REQ-4.3); the impl lives on the
/// caller's side of the boundary (Persistence.ControlPlane).
/// </summary>
public interface IProfileLookup
{
    /// <summary>True when the referenced row exists AND is active — the invariant an FK assignment must satisfy.</summary>
    Task<bool> ExistsActiveAsync(ProfileField field, Guid id, CancellationToken cancellationToken);

    /// <summary>Resolves an FK id to its display reference for the admin-detail view; null when the id is unknown.</summary>
    Task<ProfileRef?> GetRefAsync(ProfileField field, Guid id, CancellationToken cancellationToken);
}

public static class ProfileValidation
{
    /// <summary>Rejects any non-null org-profile FK that does not reference an existing, ACTIVE row
    /// (<see cref="ArgumentException"/> -> 400). Shared by the create-invite and edit-profile handlers.</summary>
    public static async Task ValidateProfileFksAsync(
        this IProfileLookup profiles,
        Guid? positionId, Guid? officeId, Guid? levelId, Guid? divisionId, CancellationToken cancellationToken)
    {
        if (positionId is { } pid && !await profiles.ExistsActiveAsync(ProfileField.Position, pid, cancellationToken))
            throw new ArgumentException("The specified position does not exist or is inactive.");
        if (officeId is { } oid && !await profiles.ExistsActiveAsync(ProfileField.Office, oid, cancellationToken))
            throw new ArgumentException("The specified office does not exist or is inactive.");
        if (levelId is { } lid && !await profiles.ExistsActiveAsync(ProfileField.Level, lid, cancellationToken))
            throw new ArgumentException("The specified level does not exist or is inactive.");
        if (divisionId is { } did && !await profiles.ExistsActiveAsync(ProfileField.Division, did, cancellationToken))
            throw new ArgumentException("The specified division does not exist or is inactive.");
    }
}
