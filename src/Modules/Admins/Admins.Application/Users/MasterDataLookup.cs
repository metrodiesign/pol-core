using MasterData.Domain;
using MasterData.Domain.Divisions;
using MasterData.Domain.Levels;
using MasterData.Domain.Offices;
using MasterData.Domain.Positions;

namespace Admins.Application.Users;

/// <summary>A resolved master reference embedded in an admin's detail (id + code + display name).</summary>
public sealed record MasterRef(Guid Id, string Code, string Name);

/// <summary>
/// Admins' own port over the MasterData reference lists (design.md §1) — existence/lookup is a caller need, not a
/// MasterData use case, so it lives here rather than on <c>MasterData.Application.IMasterDataStore</c>. Precedent:
/// <c>Admins.Infrastructure</c> already queries <c>iam.Roles</c> directly with <c>Iam.Domain</c> types the same way.
/// </summary>
public interface IMasterDataLookup
{
    /// <summary>True when the master exists AND is active — the invariant an FK assignment must satisfy.</summary>
    Task<bool> ExistsActiveAsync<T>(Guid id, CancellationToken cancellationToken) where T : MasterDataItem;

    /// <summary>Resolves an FK id to its display reference for the admin-detail view; null when the id is unknown.</summary>
    Task<MasterRef?> GetRefAsync<T>(Guid id, CancellationToken cancellationToken) where T : MasterDataItem;
}

public static class MasterProfileValidation
{
    /// <summary>Rejects any non-null org-profile FK that does not reference an existing, ACTIVE master
    /// (<see cref="ArgumentException"/> -> 400). Shared by the create-invite and edit-profile handlers.</summary>
    public static async Task ValidateProfileFksAsync(
        this IMasterDataLookup masters,
        Guid? positionId, Guid? officeId, Guid? levelId, Guid? divisionId, CancellationToken cancellationToken)
    {
        if (positionId is { } pid && !await masters.ExistsActiveAsync<Position>(pid, cancellationToken))
            throw new ArgumentException("The specified position does not exist or is inactive.");
        if (officeId is { } oid && !await masters.ExistsActiveAsync<Office>(oid, cancellationToken))
            throw new ArgumentException("The specified office does not exist or is inactive.");
        if (levelId is { } lid && !await masters.ExistsActiveAsync<Level>(lid, cancellationToken))
            throw new ArgumentException("The specified level does not exist or is inactive.");
        if (divisionId is { } did && !await masters.ExistsActiveAsync<Division>(did, cancellationToken))
            throw new ArgumentException("The specified division does not exist or is inactive.");
    }
}
