using Merchants.Application;
using Merchants.Domain;

namespace Merchants.Tests;

/// <summary>
/// The callback login resolution (REQ-9.4/9.6): an unknown subject is NotFound (a registration ticket only — never
/// self-provisioned); a PendingApproval/Rejected/Suspended user maps to its branch outcome with no resolution; an
/// Active user yields a resolution carrying the bound merchant + the effective permission set resolved scoped to that
/// merchant (REQ-16.4/17.1). The Active branch reads MerchantUser.MerchantId directly (the former separate
/// assignment lookup is absorbed) and must pass it to the permission union.
/// </summary>
public sealed class ResolveLoginHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid MerchantId = Guid.Parse("d2222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Unknown_subject_is_NotFound_never_self_provisioned()
    {
        var result = await Handle(account: null);
        Assert.Equal(MerchantUserLoginOutcome.NotFound, result.Outcome);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public async Task Pending_user_maps_to_PendingApproval()
    {
        var result = await Handle(Pending());
        Assert.Equal(MerchantUserLoginOutcome.PendingApproval, result.Outcome);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public async Task Rejected_user_maps_to_Rejected()
    {
        var user = Pending();
        user.Reject(Now);
        var result = await Handle(user);
        Assert.Equal(MerchantUserLoginOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task Suspended_user_maps_to_Suspended_deny()
    {
        var account = Pending();
        account.Approve(MerchantId, Now);
        account.Suspend(Now);
        var result = await Handle(account);
        Assert.Equal(MerchantUserLoginOutcome.Suspended, result.Outcome);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public async Task Active_user_yields_a_resolution_with_merchant_and_effective_permissions_scoped_to_that_merchant()
    {
        var account = Pending();
        account.Approve(MerchantId, Now);
        var roles = new FakeRoles("product.create", "payment.create");

        var result = await Handle(account, roles);

        Assert.Equal(MerchantUserLoginOutcome.Active, result.Outcome);
        var resolution = Assert.IsType<MerchantUserResolution>(result.Resolution);
        Assert.Equal(account.Id, resolution.MerchantUserId);
        Assert.Equal(MerchantId, resolution.MerchantId);
        Assert.Equal(account.Email, resolution.Email);
        Assert.Equal(new HashSet<string> { "product.create", "payment.create" }, resolution.Permissions);
        // the union was asked scoped to the account's OWN MerchantId (REQ-16.4)
        Assert.Equal((account.Id, MerchantId), roles.LastQuery);
    }

    private static MerchantUser Pending() => MerchantUser.Register("google-sub", "p@org.com", Now);

    private static Task<MerchantUserLoginResult> Handle(MerchantUser? account, FakeRoles? roles = null) =>
        new ResolveLoginHandler(new FakeAccounts(account), roles ?? new FakeRoles())
            .Handle(new ResolveLoginQuery("google-sub"), default).AsTask();

    private sealed class FakeAccounts(MerchantUser? account) : IMerchantUserRepository
    {
        public Task<MerchantUser?> FindBySubjectAsync(string subject, CancellationToken ct) => Task.FromResult(account);
        public Task<MerchantUser?> FindByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public void Add(MerchantUser a) => throw new NotSupportedException();
    }

    private sealed class FakeRoles(params string[] permissions) : IMerchantUserRoleRepository
    {
        private readonly IReadOnlySet<string> _permissions = permissions.ToHashSet(StringComparer.Ordinal);
        public (Guid User, Guid Merchant)? LastQuery { get; private set; }

        public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid merchantUserId, Guid merchantId, CancellationToken ct)
        {
            LastQuery = (merchantUserId, merchantId);
            return Task.FromResult(_permissions);
        }

        // Unused by ResolveLoginHandler.
        public void Add(MerchantUserRoleDefinition role) => throw new NotSupportedException();
        public void Remove(MerchantUserRoleDefinition role) => throw new NotSupportedException();
        public void AddAssignment(MerchantUserRoleAssignment assignment) => throw new NotSupportedException();
        public void RemoveAssignment(MerchantUserRoleAssignment assignment) => throw new NotSupportedException();
        public Task<MerchantUserRoleDefinition?> GetByCodeAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CodeExistsAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<MerchantUserRoleListItem>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<MerchantUserRoleListItem?> GetListItemByCodeAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken ct) => throw new NotSupportedException();
        public Task<MerchantUserRoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<MerchantUserPermissionCatalogResult> ListCatalogAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid merchantUserId, Guid merchantId, CancellationToken ct) => throw new NotSupportedException();
    }
}
