using Positions.Application;
using Positions.Domain;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Positions;

/// <summary>Position CRUD over <see cref="ControlPlaneDbContext"/> — typed split of the retired generic
/// master-data store (masterdata-split), semantics unchanged. Writes commit through the keyed <c>"admin"</c>
/// <see cref="IUnitOfWork"/> (<c>ControlPlaneUnitOfWork</c>), never DbContext.SaveChanges directly.</summary>
internal sealed class PositionStore : IPositionStore
{
    private readonly ControlPlaneDbContext _db;
    private readonly IUnitOfWork _unitOfWork;

    public PositionStore(ControlPlaneDbContext db, IUnitOfWork unitOfWork)
    {
        _db = db;
        _unitOfWork = unitOfWork;
    }

    public async Task<PagedResult<PositionItem>> ListAsync(int page, int limit, string? search, CancellationToken cancellationToken)
    {
        IQueryable<Position> src = _db.Positions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{SfsLike.Escape(search.Trim())}%";   // escape %/_ so they match literally (REQ-9.2)
            src = src.Where(m => EF.Functions.Like(m.Name, term, "\\") || EF.Functions.Like(m.Code, term, "\\"));
        }

        long total = await src.LongCountAsync(cancellationToken);   // count after search, before paging
        int skip = (int)Math.Min((long)(page - 1) * limit, int.MaxValue);
        var items = await src
            .OrderBy(m => m.Name)
            .Skip(skip)
            .Take(limit)
            .Select(m => new PositionItem(m.Id, m.Code, m.Name, m.Status, m.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<PositionItem>(items, page, limit, total);
    }

    public Task<PositionItem> CreateAsync(string code, string name, CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var entity = Position.Create(code, name);   // slug/trim invariants -> ArgumentException -> 400, as before
            // Pre-check for a clean 409 message; the unique Code index is the race-safe backstop.
            if (await _db.Positions.AnyAsync(m => m.Code == entity.Code, ct))
                throw new ConflictException($"A record with code '{entity.Code}' already exists.", "immutable_code");
            _db.Positions.Add(entity);
            await _unitOfWork.SaveChangesAsync(ct);
            return new PositionItem(entity.Id, entity.Code, entity.Name, entity.Status, entity.Version);
        }, cancellationToken);

    public Task<PositionItem> UpdateAsync(
        Guid id, string name, PositionStatus status, long expectedVersion, CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var entity = await _db.Positions.FirstOrDefaultAsync(m => m.Id == id, ct)
                ?? throw new NotFoundException("The record was not found.");
            if (entity.Version != expectedVersion)
                throw new ConflictException("The record changed after it was loaded.", "state_conflict");
            entity.Rename(name);
            if (status == PositionStatus.Active) entity.Activate(); else entity.Deactivate();
            entity.BumpVersion();
            await _unitOfWork.SaveChangesAsync(ct);
            return new PositionItem(entity.Id, entity.Code, entity.Name, entity.Status, entity.Version);
        }, cancellationToken);

    public async Task<PositionItem> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _db.Positions.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException("The record was not found.");
        return new PositionItem(entity.Id, entity.Code, entity.Name, entity.Status, entity.Version);
    }

    public Task<PositionItem> DeactivateAsync(Guid id, long expectedVersion, CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var entity = await _db.Positions.FirstOrDefaultAsync(m => m.Id == id, ct)
                ?? throw new NotFoundException("The record was not found.");
            if (entity.Version != expectedVersion)
                throw new ConflictException("The record changed after it was loaded.", "state_conflict");
            entity.Deactivate();
            entity.BumpVersion();
            await _unitOfWork.SaveChangesAsync(ct);
            return new PositionItem(entity.Id, entity.Code, entity.Name, entity.Status, entity.Version);
        }, cancellationToken);
}
