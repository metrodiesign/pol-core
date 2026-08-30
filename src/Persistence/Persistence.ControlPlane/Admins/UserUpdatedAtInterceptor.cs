using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Persistence.ControlPlane.Admins;

/// <summary>Stamps <see cref="User.UpdatedAt"/> on every Modified admin row at save time. One seam instead of
/// threading a clock through each aggregate method: every mutation path (status/tier/profile/bind and the
/// related-aggregate <c>BumpAuthorizationVersion</c> callers) reaches the database only through
/// <c>ControlPlaneDbContext.SaveChanges</c>, so this cannot be bypassed by a new caller.</summary>
internal sealed class UserUpdatedAtInterceptor(IClock clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
            return;
        var now = clock.UtcNow;
        foreach (var entry in context.ChangeTracker.Entries<User>())
        {
            if (entry.State == EntityState.Modified)
                entry.Property(x => x.UpdatedAt).CurrentValue = now;
        }
    }
}
