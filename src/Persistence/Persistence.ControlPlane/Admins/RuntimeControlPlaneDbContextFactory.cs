using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Admins;

/// <summary>Runtime factory used only by conflict recovery. It creates a new context per read and injects a
/// deny-all write authorizer so accidental writes from the recovery path fail closed.</summary>
internal sealed class RuntimeControlPlaneDbContextFactory : IDbContextFactory<ControlPlaneDbContext>
{
    private readonly DbContextOptions<ControlPlaneDbContext> _options;
    private readonly ISecurityTelemetry _telemetry;

    public RuntimeControlPlaneDbContextFactory(
        string connectionString, IServiceProvider serviceProvider, ISecurityTelemetry telemetry)
    {
        _options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(170))
            .UseApplicationServiceProvider(serviceProvider)
            .Options;
        _telemetry = telemetry;
    }

    public ControlPlaneDbContext CreateDbContext() =>
        new(_options, ReadOnlyAuthorizer.Instance, _telemetry);

    public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    private sealed class ReadOnlyAuthorizer : IWriteAuthorizer
    {
        public static readonly ReadOnlyAuthorizer Instance = new();

        private ReadOnlyAuthorizer() { }

        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => false;
    }
}
