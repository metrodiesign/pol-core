using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Application.AdminControlPlane;

namespace Persistence.MerchantRuntime.Payments;

internal sealed class AdminAuthorizationLeaseRow
{
    public Guid Id { get; private set; }
    public int Status { get; private set; }
    public long AuthorizationVersion { get; private set; }
}

internal sealed class AdminAuthorizationLeaseRowConfiguration(MerchantRuntimeDbContext context)
    : IEntityTypeConfiguration<AdminAuthorizationLeaseRow>
{
    public void Configure(EntityTypeBuilder<AdminAuthorizationLeaseRow> builder)
    {
        // SQLite ignores schemas, so its shared-context test database needs a distinct physical alias.
        // SQL Server maps this projection to the existing admin.Users row as required.
        var table = context.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? "AdminAuthorizationLeaseUsers"
            : "Users";
        builder.ToTable(table, SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.AuthorizationVersion).IsConcurrencyToken().IsRequired();
    }
}

internal sealed class MerchantRuntimeAuthorizationLease(
    MerchantRuntimeDbContext db,
    ISecurityTelemetry telemetry) : IMerchantRuntimeAuthorizationLease
{
    private const int ActiveStatus = 1;

    public async Task VerifyAsync(AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Authorization lease requires an active Merchant Runtime transaction.");

        var caller = await PlatformReadGuard.ReadAsync(ct => db.Set<AdminAuthorizationLeaseRow>()
            .SingleOrDefaultAsync(x => x.Id == access.ActorId, ct), cancellationToken);
        if (caller is null || caller.Status != ActiveStatus
            || caller.AuthorizationVersion != access.AuthorizationVersion)
        {
            telemetry.Emit(new DenialEvent(
                DenialCategory.AdminRevalidationDenial, "admin", access.ActorId, TargetMerchant: null,
                nameof(AdminAuthorizationLeaseRow), "MerchantRuntimeAuthorizationLease.Verify",
                "Admin authorization lease was stale or inactive.", CorrelationId.Current, DateTime.UtcNow));
            throw new AccessDeniedException("Admin authorization changed; refresh the session.", "authorization_stale");
        }

        db.Entry(caller).Property(x => x.AuthorizationVersion).IsModified = true;
    }
}
