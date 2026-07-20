using BuildingBlocks.Application;

namespace Levels.Application;

/// <summary>A level row as the management endpoints render it.</summary>
public sealed record LevelItem(Guid Id, string Code, string Name, bool IsActive);

/// <summary>
/// Runtime CRUD over the level master list. Simple control-plane reference data, so it deliberately
/// bypasses Mediator — but it still commits through the keyed <c>"admin"</c> <see cref="IUnitOfWork"/>.
/// Existence/lookup for admin-profile FKs is NOT here — that is <c>Admins</c>' own port
/// (<c>Admins.Application.Users.IProfileLookup</c>), a caller need, not a use case of this module.
/// </summary>
public interface ILevelStore
{
    /// <summary>Paged list, optional case-insensitive contains-search over Code/Name, ordered by Name.</summary>
    Task<PagedResult<LevelItem>> ListAsync(int page, int limit, string? search, CancellationToken cancellationToken);

    /// <summary>Creates + persists a new level. A duplicate <c>Code</c> is rejected (<see cref="ConflictException"/> 409).</summary>
    Task<LevelItem> CreateAsync(string code, string name, CancellationToken cancellationToken);

    /// <summary>Renames + toggles active on an existing level. Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<LevelItem> UpdateAsync(Guid id, string name, bool isActive, CancellationToken cancellationToken);

    /// <summary>Reads a single level by id. Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<LevelItem> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Soft-deactivates (sets IsActive=false) without touching Code/Name — never a hard delete (the
    /// <c>AdminAccount</c> FK is Restrict). Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<LevelItem> DeactivateAsync(Guid id, CancellationToken cancellationToken);
}
