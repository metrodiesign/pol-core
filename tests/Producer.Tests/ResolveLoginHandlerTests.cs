using Producer.Application;
using Producer.Domain;

namespace Producer.Tests;

/// <summary>
/// The callback login resolution (REQ-9.4/9.6): an unknown subject is NotFound (a registration ticket only — never
/// self-provisioned); a PendingApproval/Rejected/Suspended user maps to its branch outcome with no resolution; an
/// Active user yields a resolution carrying the bound tenant + the effective permission set resolved scoped to that
/// tenant (REQ-16.4/17.1). The Active branch must pass the user's OWN tenant id to the permission union.
/// </summary>
public sealed class ResolveLoginHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantId = Guid.Parse("d2222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Unknown_subject_is_NotFound_never_self_provisioned()
    {
        var result = await Handle(user: null);
        Assert.Equal(ProducerLoginOutcome.NotFound, result.Outcome);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public async Task Pending_user_maps_to_PendingApproval()
    {
        var result = await Handle(Pending());
        Assert.Equal(ProducerLoginOutcome.PendingApproval, result.Outcome);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public async Task Rejected_user_maps_to_Rejected()
    {
        var user = Pending();
        user.Reject(Now);
        var result = await Handle(user);
        Assert.Equal(ProducerLoginOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task Suspended_user_maps_to_Suspended_deny()
    {
        var user = Pending();
        user.Approve(TenantId, Now);
        user.Suspend(Now);
        var result = await Handle(user);
        Assert.Equal(ProducerLoginOutcome.Suspended, result.Outcome);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public async Task Active_user_yields_a_resolution_with_tenant_and_effective_permissions_scoped_to_that_tenant()
    {
        var user = Pending();
        user.Approve(TenantId, Now);
        var roles = new FakeRoles("product.create", "payment.create");

        var result = await Handle(user, roles);

        Assert.Equal(ProducerLoginOutcome.Active, result.Outcome);
        var resolution = Assert.IsType<ProducerResolution>(result.Resolution);
        Assert.Equal(user.Id, resolution.TenantUserId);
        Assert.Equal(TenantId, resolution.TenantId);
        Assert.Equal(user.Email, resolution.Email);
        Assert.Equal(new HashSet<string> { "product.create", "payment.create" }, resolution.Permissions);
        // the union was asked scoped to the user's OWN tenant id (REQ-16.4)
        Assert.Equal((user.Id, TenantId), roles.LastQuery);
    }

    private static TenantUser Pending() => TenantUser.Register("google-sub", "p@org.com", Now);

    private static Task<ProducerLoginResult> Handle(TenantUser? user, FakeRoles? roles = null) =>
        new ResolveLoginHandler(new FakeUsers(user), roles ?? new FakeRoles())
            .Handle(new ResolveLoginQuery("google-sub"), default).AsTask();

    private sealed class FakeUsers(TenantUser? user) : ITenantUserRepository
    {
        public Task<TenantUser?> FindBySubjectAsync(string subject, CancellationToken ct) => Task.FromResult(user);
        public Task<TenantUser?> FindByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public void Add(TenantUser u) => throw new NotSupportedException();
    }

    private sealed class FakeRoles(params string[] permissions) : IProducerRoleRepository
    {
        private readonly IReadOnlySet<string> _permissions = permissions.ToHashSet(StringComparer.Ordinal);
        public (Guid User, Guid Tenant)? LastQuery { get; private set; }

        public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid tenantUserId, Guid tenantId, CancellationToken ct)
        {
            LastQuery = (tenantUserId, tenantId);
            return Task.FromResult(_permissions);
        }

        // Unused by ResolveLoginHandler.
        public void Add(ProducerRole role) => throw new NotSupportedException();
        public void Remove(ProducerRole role) => throw new NotSupportedException();
        public void AddAssignment(ProducerRoleAssignment assignment) => throw new NotSupportedException();
        public void RemoveAssignment(ProducerRoleAssignment assignment) => throw new NotSupportedException();
        public Task<ProducerRole?> GetByCodeAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CodeExistsAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProducerRoleListItem>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ProducerRoleListItem?> GetListItemByCodeAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid tenantUserId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProducerRoleAssignment?> GetAssignmentAsync(Guid tenantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AssignmentExistsAsync(Guid tenantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ProducerPermissionCatalogResult> ListCatalogAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid tenantUserId, Guid tenantId, CancellationToken ct) => throw new NotSupportedException();
    }
}
