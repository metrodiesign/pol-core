using Admins.Domain;
using BuildingBlocks.Application;

namespace Admins.Application;

/// <summary>A master row as the management endpoints render it.</summary>
public sealed record MasterItem(Guid Id, string Code, string Name, bool IsActive);

/// <summary>A resolved master reference embedded in an admin's detail (id + code + display name).</summary>
public sealed record MasterRef(Guid Id, string Code, string Name);

/// <summary>
/// Runtime CRUD + lookup over the four admin-profile master lists (Position/Office/Level/Division). This is
/// simple control-plane reference data, so it deliberately bypasses Mediator — but it still commits through
/// the keyed <c>"admin"</c> <see cref="IUnitOfWork"/>. One store, parameterised by the concrete master type.
/// </summary>
public interface IMasterDataStore
{
    /// <summary>Paged list, optional case-insensitive contains-search over Code/Name, ordered by Name.</summary>
    Task<PagedResult<MasterItem>> ListAsync<T>(int page, int limit, string? search, CancellationToken cancellationToken)
        where T : MasterData;

    /// <summary>Persists a new master. A duplicate <c>Code</c> is rejected (<see cref="ConflictException"/> 409).</summary>
    Task<MasterItem> CreateAsync<T>(T entity, CancellationToken cancellationToken) where T : MasterData;

    /// <summary>Renames + toggles active on an existing master. Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<MasterItem> UpdateAsync<T>(Guid id, string name, bool isActive, CancellationToken cancellationToken)
        where T : MasterData;

    /// <summary>True when the master exists AND is active — the invariant an FK assignment must satisfy.</summary>
    Task<bool> ExistsActiveAsync<T>(Guid id, CancellationToken cancellationToken) where T : MasterData;

    /// <summary>Resolves an FK id to its display reference for the admin-detail view; null when the id is unknown.</summary>
    Task<MasterRef?> GetRefAsync<T>(Guid id, CancellationToken cancellationToken) where T : MasterData;
}

public static class MasterProfileValidation
{
    /// <summary>Rejects any non-null org-profile FK that does not reference an existing, ACTIVE master
    /// (<see cref="ArgumentException"/> -> 400). Shared by the create-invite and edit-profile handlers.</summary>
    public static async Task ValidateProfileFksAsync(
        this IMasterDataStore masters,
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
