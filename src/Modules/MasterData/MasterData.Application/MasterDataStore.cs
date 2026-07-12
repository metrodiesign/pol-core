using MasterData.Domain;
using BuildingBlocks.Application;

namespace MasterData.Application;

/// <summary>A master row as the management endpoints render it.</summary>
public sealed record MasterItem(Guid Id, string Code, string Name, bool IsActive);

/// <summary>
/// Runtime CRUD over the four admin-profile master lists (Position/Office/Level/Division). This is simple
/// control-plane reference data, so it deliberately bypasses Mediator — but it still commits through the keyed
/// <c>"admin"</c> <see cref="IUnitOfWork"/>. One store, parameterised by the concrete master type. Existence/lookup
/// (<c>ExistsActiveAsync</c>/<c>GetRefAsync</c>) is NOT here — that is <c>Admins</c>' own port
/// (<c>Admins.Application.Users.IMasterDataLookup</c>), since it is a caller need, not a MasterData use case.
/// </summary>
public interface IMasterDataStore
{
    /// <summary>Paged list, optional case-insensitive contains-search over Code/Name, ordered by Name.</summary>
    Task<PagedResult<MasterItem>> ListAsync<T>(int page, int limit, string? search, CancellationToken cancellationToken)
        where T : MasterDataItem;

    /// <summary>Persists a new master. A duplicate <c>Code</c> is rejected (<see cref="ConflictException"/> 409).</summary>
    Task<MasterItem> CreateAsync<T>(T entity, CancellationToken cancellationToken) where T : MasterDataItem;

    /// <summary>Renames + toggles active on an existing master. Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<MasterItem> UpdateAsync<T>(Guid id, string name, bool isActive, CancellationToken cancellationToken)
        where T : MasterDataItem;
}
