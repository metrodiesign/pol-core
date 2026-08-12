using BuildingBlocks.Application;

using Positions.Domain;

namespace Positions.Application;

/// <summary>A position row as the management endpoints render it.</summary>
public sealed record PositionItem(Guid Id, string Code, string Name, PositionStatus Status, long Version);

/// <summary>
/// Runtime CRUD over the position master list. Simple control-plane reference data, so it deliberately
/// bypasses Mediator — but it still commits through the keyed <c>"admin"</c> <see cref="IUnitOfWork"/>.
/// Existence/lookup for admin-profile FKs is NOT here — that is <c>Admins</c>' own port
/// (<c>Admins.Application.Users.IProfileLookup</c>), a caller need, not a use case of this module.
/// </summary>
public interface IPositionStore
{
    /// <summary>Paged list, optional case-insensitive contains-search over Code/Name, ordered by Name.</summary>
    Task<PagedResult<PositionItem>> ListAsync(int page, int limit, string? search, CancellationToken cancellationToken);

    /// <summary>Creates + persists a new position. A duplicate <c>Code</c> is rejected (<see cref="ConflictException"/> 409).</summary>
    Task<PositionItem> CreateAsync(string code, string name, CancellationToken cancellationToken);

    /// <summary>Renames + toggles active on an existing position. Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<PositionItem> UpdateAsync(Guid id, string name, PositionStatus status, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Reads a single position by id. Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<PositionItem> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Soft-deactivates (sets IsActive=false) without touching Code/Name — never a hard delete (the
    /// <c>AdminAccount</c> FK is Restrict). Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<PositionItem> DeactivateAsync(Guid id, long expectedVersion, CancellationToken cancellationToken);
}
