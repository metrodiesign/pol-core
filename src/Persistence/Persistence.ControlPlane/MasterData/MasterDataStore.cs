using MasterData.Application;
using MasterData.Domain;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.MasterData;

/// <summary>Master-data CRUD over <see cref="ControlPlaneDbContext"/> — moved from
/// <c>MasterData.Infrastructure.Persistence.MasterDataStore</c> (task 8.5.1), unchanged. Writes commit through
/// the keyed <c>"admin"</c> <see cref="IUnitOfWork"/> (<c>ControlPlaneUnitOfWork</c>), never DbContext.SaveChanges
/// directly.</summary>
internal sealed class MasterDataStore : IMasterDataStore
{
    private readonly ControlPlaneDbContext _db;
    private readonly IUnitOfWork _unitOfWork;

    public MasterDataStore(ControlPlaneDbContext db, IUnitOfWork unitOfWork)
    {
        _db = db;
        _unitOfWork = unitOfWork;
    }

    public async Task<PagedResult<MasterItem>> ListAsync<T>(int page, int limit, string? search, CancellationToken cancellationToken)
        where T : MasterDataItem
    {
        IQueryable<T> src = _db.Set<T>().AsNoTracking();
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
            .Select(m => new MasterItem(m.Id, m.Code, m.Name, m.IsActive))
            .ToListAsync(cancellationToken);

        return new PagedResult<MasterItem>(items, page, limit, total);
    }

    public Task<MasterItem> CreateAsync<T>(T entity, CancellationToken cancellationToken) where T : MasterDataItem =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // Pre-check for a clean 409 message; the unique Code index is the race-safe backstop.
            if (await _db.Set<T>().AnyAsync(m => m.Code == entity.Code, ct))
                throw new ConflictException($"A record with code '{entity.Code}' already exists.");
            _db.Set<T>().Add(entity);
            await _unitOfWork.SaveChangesAsync(ct);
            return new MasterItem(entity.Id, entity.Code, entity.Name, entity.IsActive);
        }, cancellationToken);

    public Task<MasterItem> UpdateAsync<T>(Guid id, string name, bool isActive, CancellationToken cancellationToken)
        where T : MasterDataItem =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var entity = await _db.Set<T>().FirstOrDefaultAsync(m => m.Id == id, ct)
                ?? throw new NotFoundException("The record was not found.");
            entity.Rename(name);
            if (isActive) entity.Activate(); else entity.Deactivate();
            await _unitOfWork.SaveChangesAsync(ct);
            return new MasterItem(entity.Id, entity.Code, entity.Name, entity.IsActive);
        }, cancellationToken);
}
