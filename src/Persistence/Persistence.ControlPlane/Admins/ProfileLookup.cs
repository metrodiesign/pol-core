using Admins.Application.Users;
using Divisions.Domain;
using Levels.Domain;
using Microsoft.EntityFrameworkCore;
using Offices.Domain;
using Positions.Domain;

namespace Persistence.ControlPlane.Admins;

/// <summary>Implements Admins' <see cref="IProfileLookup"/> port over <see cref="ControlPlaneDbContext"/> —
/// one query arm per <see cref="ProfileField"/> DbSet (the enum split of the retired generic master-data
/// lookup, masterdata-split). The reference modules never know Admins exists; this impl lives on the caller's
/// side of the boundary (REQ-4.4/4.5).</summary>
internal sealed class ProfileLookup : IProfileLookup
{
    private readonly ControlPlaneDbContext _db;

    public ProfileLookup(ControlPlaneDbContext db) => _db = db;

    public Task<bool> ExistsActiveAsync(ProfileField field, Guid id, CancellationToken cancellationToken) =>
        field switch
        {
            ProfileField.Position => _db.Positions.AnyAsync(m => m.Id == id && m.Status == PositionStatus.Active, cancellationToken),
            ProfileField.Office => _db.Offices.AnyAsync(m => m.Id == id && m.Status == OfficeStatus.Active, cancellationToken),
            ProfileField.Level => _db.Levels.AnyAsync(m => m.Id == id && m.Status == LevelStatus.Active, cancellationToken),
            ProfileField.Division => _db.Divisions.AnyAsync(m => m.Id == id && m.Status == DivisionStatus.Active, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    public Task<ProfileRef?> GetRefAsync(ProfileField field, Guid id, CancellationToken cancellationToken) =>
        field switch
        {
            ProfileField.Position => _db.Positions.AsNoTracking().Where(m => m.Id == id)
                .Select(m => new ProfileRef(m.Id, m.Code, m.Name)).FirstOrDefaultAsync(cancellationToken),
            ProfileField.Office => _db.Offices.AsNoTracking().Where(m => m.Id == id)
                .Select(m => new ProfileRef(m.Id, m.Code, m.Name)).FirstOrDefaultAsync(cancellationToken),
            ProfileField.Level => _db.Levels.AsNoTracking().Where(m => m.Id == id)
                .Select(m => new ProfileRef(m.Id, m.Code, m.Name)).FirstOrDefaultAsync(cancellationToken),
            ProfileField.Division => _db.Divisions.AsNoTracking().Where(m => m.Id == id)
                .Select(m => new ProfileRef(m.Id, m.Code, m.Name)).FirstOrDefaultAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
}
