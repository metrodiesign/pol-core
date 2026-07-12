using Admins.Application.Users;
using MasterData.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Admins.Infrastructure.Persistence.Users;

/// <summary>Implements Admins' <see cref="IMasterDataLookup"/> port by querying <c>MasterData.Domain</c> types
/// directly via <c>_db.Set&lt;T&gt;()</c> on the shared <see cref="PolDbContext"/> — the same pattern
/// <c>RoleRepository</c> uses for <c>iam.Roles</c>. MasterData never knows Admins exists; this impl lives on the
/// caller's side of the boundary (design.md §1, REQ-4.4).</summary>
public sealed class MasterDataLookup : IMasterDataLookup
{
    private readonly PolDbContext _db;

    public MasterDataLookup(PolDbContext db) => _db = db;

    public Task<bool> ExistsActiveAsync<T>(Guid id, CancellationToken cancellationToken) where T : MasterDataItem =>
        _db.Set<T>().AnyAsync(m => m.Id == id && m.IsActive, cancellationToken);

    public Task<MasterRef?> GetRefAsync<T>(Guid id, CancellationToken cancellationToken) where T : MasterDataItem =>
        _db.Set<T>().AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new MasterRef(m.Id, m.Code, m.Name))
            .FirstOrDefaultAsync(cancellationToken);
}
