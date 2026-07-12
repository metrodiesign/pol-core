using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Application;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;

namespace Merchants.Infrastructure.Persistence;

/// <summary>EF Core repository for <see cref="Merchant"/> over the shared data plane. The
/// host binds it to the pol_admin (RLS-bypass) connection so provisioning + read-back see every merchant.</summary>
public sealed class MerchantRepository : IMerchantRepository
{
    private readonly PolDbContext _db;

    public MerchantRepository(PolDbContext db) => _db = db;

    public void Add(Merchant merchant) => _db.Set<Merchant>().Add(merchant);

    public Task<Merchant?> GetByCodeAsync(string normalizedCode, CancellationToken cancellationToken) =>
        _db.Set<Merchant>().FirstOrDefaultAsync(x => x.Code == normalizedCode, cancellationToken);

    public Task<bool> ExistsByCodeAsync(string normalizedCode, CancellationToken cancellationToken) =>
        _db.Set<Merchant>().AnyAsync(x => x.Code == normalizedCode, cancellationToken);
}
