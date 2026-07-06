using Admin.Application;
using Admin.Domain;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Admin.Infrastructure.Persistence;

/// <summary>Master-data CRUD over the keyed pol_admin (RLS-bypass) context — control-plane reference lists.
/// Writes commit through the keyed <c>"admin"</c> <see cref="IUnitOfWork"/> (S2), never DbContext.SaveChanges.</summary>
public sealed class MasterDataStore : IMasterDataStore
{
    private readonly ProducerDbContext _db;
    private readonly IUnitOfWork _unitOfWork;

    public MasterDataStore(ProducerDbContext db, IUnitOfWork unitOfWork)
    {
        _db = db;
        _unitOfWork = unitOfWork;
    }

    public async Task<PagedResult<MasterItem>> ListAsync<T>(int page, int limit, string? search, CancellationToken cancellationToken)
        where T : MasterData
    {
        IQueryable<T> src = _db.Set<T>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            src = src.Where(m => EF.Functions.Like(m.Name, term) || EF.Functions.Like(m.Code, term));
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

    public Task<MasterItem> CreateAsync<T>(T entity, CancellationToken cancellationToken) where T : MasterData =>
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
        where T : MasterData =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var entity = await _db.Set<T>().FirstOrDefaultAsync(m => m.Id == id, ct)
                ?? throw new NotFoundException("The record was not found.");
            entity.Rename(name);
            if (isActive) entity.Activate(); else entity.Deactivate();
            await _unitOfWork.SaveChangesAsync(ct);
            return new MasterItem(entity.Id, entity.Code, entity.Name, entity.IsActive);
        }, cancellationToken);

    public Task<bool> ExistsActiveAsync<T>(Guid id, CancellationToken cancellationToken) where T : MasterData =>
        _db.Set<T>().AnyAsync(m => m.Id == id && m.IsActive, cancellationToken);

    public Task<MasterRef?> GetRefAsync<T>(Guid id, CancellationToken cancellationToken) where T : MasterData =>
        _db.Set<T>().AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new MasterRef(m.Id, m.Code, m.Name))
            .FirstOrDefaultAsync(cancellationToken);
}
