extern alias ApiHost;

using ApiHost::Api.Iam;
using Iam.Application.Roles;
using Persistence.ControlPlane;
using Persistence.MerchantUser;

namespace Hosts.Tests;

/// <summary>
/// <see cref="HostRoleAssignmentCounter"/> scopes counts to the caller's <see cref="RoleSideContext"/>
/// (Codex P1): a Merchant console must see only its OWN merchant's assignment rows for a shared role — the
/// global total would leak how many users OTHER merchants have bound (REQ-3.6). Task 8.5.7 split the counter
/// into two narrow per-cluster ports (<c>IAdminRoleAssignmentCountReader</c>/<c>IMerchantRoleAssignmentCountReader</c>)
/// since <c>ControlPlaneDbContext</c>/<c>MerchantUserDbContext</c> are two SEPARATE runtime contexts this host
/// may not query directly — this test now pins <see cref="HostRoleAssignmentCounter"/>'s own branch/sum logic
/// in isolation via fakes of those two ports; each reader's own EF query correctness (IgnoreQueryFilters,
/// per-merchant scoping) is the concern of <c>Persistence.ControlPlane</c>/<c>Persistence.MerchantUser</c>'s
/// own coverage, not this composing class.
/// </summary>
public sealed class RoleAssignmentCounterTests
{
    private static readonly Guid MerchantA = Guid.NewGuid();
    private static readonly Guid MerchantB = Guid.NewGuid();
    private static readonly Guid SharedRoleId = Guid.NewGuid();
    private static readonly Guid UnassignedRoleId = Guid.NewGuid();

    // The shared role is bound twice in merchant A and once in merchant B (admin side has no assignments —
    // Merchant-scope roles are never admin-assignable, REQ-3.6's own invariant).
    private static HostRoleAssignmentCounter Counter() => new(
        new FakeAdminReader(),
        new FakeMerchantReader(new Dictionary<(Guid Role, Guid Merchant), int>
        {
            [(SharedRoleId, MerchantA)] = 2,
            [(SharedRoleId, MerchantB)] = 1,
        }));

    [Fact]
    public async Task Merchant_count_covers_only_its_own_merchants_assignments()
    {
        Assert.Equal(2, await Counter().CountAsync(RoleSideContext.Merchant(MerchantA), SharedRoleId, CancellationToken.None));
        Assert.Equal(1, await Counter().CountAsync(RoleSideContext.Merchant(MerchantB), SharedRoleId, CancellationToken.None));
        Assert.Equal(0, await Counter().CountAsync(RoleSideContext.Merchant(Guid.NewGuid()), SharedRoleId, CancellationToken.None));
    }

    [Fact]
    public async Task CountMany_applies_the_same_merchant_scoping_and_zero_fills_absent_roles()
    {
        var counts = await Counter().CountManyAsync(
            RoleSideContext.Merchant(MerchantA), [SharedRoleId, UnassignedRoleId], CancellationToken.None);

        Assert.Equal(2, counts[SharedRoleId]);
        Assert.Equal(0, counts[UnassignedRoleId]);
    }

    [Fact]
    public async Task Platform_count_sums_admin_and_global_merchant_assignments()
    {
        var counter = new HostRoleAssignmentCounter(
            new FakeAdminReader(new Dictionary<Guid, int> { [SharedRoleId] = 4 }),
            new FakeMerchantReader(global: new Dictionary<Guid, int> { [SharedRoleId] = 3 }));

        Assert.Equal(7, await counter.CountAsync(RoleSideContext.Platform(), SharedRoleId, CancellationToken.None));
    }

    private sealed class FakeAdminReader(IReadOnlyDictionary<Guid, int>? counts = null) : IAdminRoleAssignmentCountReader
    {
        public Task<int> CountAsync(Guid roleId, CancellationToken cancellationToken) =>
            Task.FromResult(counts?.GetValueOrDefault(roleId) ?? 0);

        public Task<IReadOnlyDictionary<Guid, int>> CountManyAsync(
            IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, int>>(
                roleIds.ToDictionary(id => id, id => counts?.GetValueOrDefault(id) ?? 0));
    }

    private sealed class FakeMerchantReader(
        IReadOnlyDictionary<(Guid Role, Guid Merchant), int>? perMerchant = null,
        IReadOnlyDictionary<Guid, int>? global = null) : IMerchantRoleAssignmentCountReader
    {
        public Task<int> CountForMerchantAsync(Guid roleId, Guid merchantId, CancellationToken cancellationToken) =>
            Task.FromResult(perMerchant?.GetValueOrDefault((roleId, merchantId)) ?? 0);

        public Task<int> CountGlobalAsync(Guid roleId, CancellationToken cancellationToken) =>
            Task.FromResult(global?.GetValueOrDefault(roleId) ?? 0);

        public Task<IReadOnlyDictionary<Guid, int>> CountManyForMerchantAsync(
            IReadOnlyCollection<Guid> roleIds, Guid merchantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, int>>(
                roleIds.ToDictionary(id => id, id => perMerchant?.GetValueOrDefault((id, merchantId)) ?? 0));

        public Task<IReadOnlyDictionary<Guid, int>> CountManyGlobalAsync(
            IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, int>>(
                roleIds.ToDictionary(id => id, id => global?.GetValueOrDefault(id) ?? 0));
    }
}
