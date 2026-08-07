using BuildingBlocks.Application;

using Divisions.Domain;

namespace Divisions.Application;

/// <summary>A division row as the management endpoints render it.</summary>
public sealed record DivisionItem(Guid Id, string Code, string Name, DivisionStatus Status);

/// <summary>
/// Runtime CRUD over the division master list. Simple control-plane reference data, so it deliberately
/// bypasses Mediator — but it still commits through the keyed <c>"admin"</c> <see cref="IUnitOfWork"/>.
/// Existence/lookup for admin-profile FKs is NOT here — that is <c>Admins</c>' own port
/// (<c>Admins.Application.Users.IProfileLookup</c>), a caller need, not a use case of this module.
/// </summary>
public interface IDivisionStore
{
    /// <summary>Paged list, optional case-insensitive contains-search over Code/Name, ordered by Name.</summary>
    Task<PagedResult<DivisionItem>> ListAsync(int page, int limit, string? search, CancellationToken cancellationToken);

    /// <summary>Creates + persists a new division. A duplicate <c>Code</c> is rejected (<see cref="ConflictException"/> 409).</summary>
    Task<DivisionItem> CreateAsync(string code, string name, CancellationToken cancellationToken);

    /// <summary>Renames + toggles active on an existing division. Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<DivisionItem> UpdateAsync(Guid id, string name, DivisionStatus status, CancellationToken cancellationToken);

    /// <summary>Reads a single division by id. Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<DivisionItem> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Soft-deactivates (sets IsActive=false) without touching Code/Name — never a hard delete (the
    /// <c>AdminAccount</c> FK is Restrict). Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<DivisionItem> DeactivateAsync(Guid id, CancellationToken cancellationToken);
}
