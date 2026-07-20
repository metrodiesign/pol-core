using Divisions.Application;
using Divisions.Domain;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Divisions;

/// <summary>Division CRUD over <see cref="ControlPlaneDbContext"/> — typed split of the retired generic
/// master-data store (masterdata-split), semantics unchanged. Writes commit through the keyed <c>"admin"</c>
/// <see cref="IUnitOfWork"/> (<c>ControlPlaneUnitOfWork</c>), never DbContext.SaveChanges directly.</summary>
internal sealed class DivisionStore : IDivisionStore
{
    private readonly ControlPlaneDbContext _db;
    private readonly IUnitOfWork _unitOfWork;

    public DivisionStore(ControlPlaneDbContext db, IUnitOfWork unitOfWork)
    {
        _db = db;
        _unitOfWork = unitOfWork;
    }

    public async Task<PagedResult<DivisionItem>> ListAsync(int page, int limit, string? search, CancellationToken cancellationToken)
    {
        IQueryable<Division> src = _db.Divisions.AsNoTracking();
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
            .Select(m => new DivisionItem(m.Id, m.Code, m.Name, m.IsActive))
            .ToListAsync(cancellationToken);

        return new PagedResult<DivisionItem>(items, page, limit, total);
    }

    public Task<DivisionItem> CreateAsync(string code, string name, CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var entity = Division.Create(code, name);   // slug/trim invariants -> ArgumentException -> 400, as before
            // Pre-check for a clean 409 message; the unique Code index is the race-safe backstop.
            if (await _db.Divisions.AnyAsync(m => m.Code == entity.Code, ct))
                throw new ConflictException($"A record with code '{entity.Code}' already exists.");
            _db.Divisions.Add(entity);
            await _unitOfWork.SaveChangesAsync(ct);
            return new DivisionItem(entity.Id, entity.Code, entity.Name, entity.IsActive);
        }, cancellationToken);

    public Task<DivisionItem> UpdateAsync(Guid id, string name, bool isActive, CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var entity = await _db.Divisions.FirstOrDefaultAsync(m => m.Id == id, ct)
                ?? throw new NotFoundException("The record was not found.");
            entity.Rename(name);
            if (isActive) entity.Activate(); else entity.Deactivate();
            await _unitOfWork.SaveChangesAsync(ct);
            return new DivisionItem(entity.Id, entity.Code, entity.Name, entity.IsActive);
        }, cancellationToken);

    public async Task<DivisionItem> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _db.Divisions.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException("The record was not found.");
        return new DivisionItem(entity.Id, entity.Code, entity.Name, entity.IsActive);
    }

    public Task<DivisionItem> DeactivateAsync(Guid id, CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var entity = await _db.Divisions.FirstOrDefaultAsync(m => m.Id == id, ct)
                ?? throw new NotFoundException("The record was not found.");
            entity.Deactivate();
            await _unitOfWork.SaveChangesAsync(ct);
            return new DivisionItem(entity.Id, entity.Code, entity.Name, entity.IsActive);
        }, cancellationToken);
}
