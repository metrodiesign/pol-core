using Admins.Application.Users;
using MasterData.Domain;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Admins;

/// <summary>Implements Admins' <see cref="IMasterDataLookup"/> port by querying <c>MasterData.Domain</c> types
/// directly via <c>_db.Set&lt;T&gt;()</c> on <see cref="ControlPlaneDbContext"/> — the same pattern
/// <see cref="RoleRepository"/> uses for <c>iam.Roles</c>. Moved from
/// <c>Admins.Infrastructure.Persistence.Users.MasterDataLookup</c> (task 8.5.1), unchanged. MasterData never
/// knows Admins exists; this impl lives on the caller's side of the boundary (design.md §1, REQ-4.4).</summary>
internal sealed class MasterDataLookup : IMasterDataLookup
{
    private readonly ControlPlaneDbContext _db;

    public MasterDataLookup(ControlPlaneDbContext db) => _db = db;

    public Task<bool> ExistsActiveAsync<T>(Guid id, CancellationToken cancellationToken) where T : MasterDataItem =>
        _db.Set<T>().AnyAsync(m => m.Id == id && m.IsActive, cancellationToken);

    public Task<MasterRef?> GetRefAsync<T>(Guid id, CancellationToken cancellationToken) where T : MasterDataItem =>
        _db.Set<T>().AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new MasterRef(m.Id, m.Code, m.Name))
            .FirstOrDefaultAsync(cancellationToken);
}
