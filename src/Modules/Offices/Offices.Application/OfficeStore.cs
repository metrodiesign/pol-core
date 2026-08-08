using BuildingBlocks.Application;

using Offices.Domain;

namespace Offices.Application;

/// <summary>A office row as the management endpoints render it.</summary>
public sealed record OfficeItem(Guid Id, string Code, string Name, OfficeStatus Status);

/// <summary>
/// Runtime CRUD over the office master list. Simple control-plane reference data, so it deliberately
/// bypasses Mediator — but it still commits through the keyed <c>"admin"</c> <see cref="IUnitOfWork"/>.
/// Existence/lookup for admin-profile FKs is NOT here — that is <c>Admins</c>' own port
/// (<c>Admins.Application.Users.IProfileLookup</c>), a caller need, not a use case of this module.
/// </summary>
public interface IOfficeStore
{
    /// <summary>Paged list, optional case-insensitive contains-search over Code/Name, ordered by Name.</summary>
    Task<PagedResult<OfficeItem>> ListAsync(int page, int limit, string? search, CancellationToken cancellationToken);

    /// <summary>Creates + persists a new office. A duplicate <c>Code</c> is rejected (<see cref="ConflictException"/> 409).</summary>
    Task<OfficeItem> CreateAsync(string code, string name, CancellationToken cancellationToken);

    /// <summary>Renames + toggles active on an existing office. Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<OfficeItem> UpdateAsync(Guid id, string name, OfficeStatus status, CancellationToken cancellationToken);

    /// <summary>Reads a single office by id. Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<OfficeItem> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Soft-deactivates (sets IsActive=false) without touching Code/Name — never a hard delete (the
    /// <c>AdminAccount</c> FK is Restrict). Unknown id -> <see cref="NotFoundException"/> 404.</summary>
    Task<OfficeItem> DeactivateAsync(Guid id, CancellationToken cancellationToken);
}
