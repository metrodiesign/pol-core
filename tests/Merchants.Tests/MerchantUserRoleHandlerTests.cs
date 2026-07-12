using BuildingBlocks.Application;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;

namespace Merchants.Tests;

/// <summary>
/// <c>SetMerchantUserRoles</c> merchant scoping (REQ-16): a target outside the acting merchant (or not Active) is
/// invisible (404, no leak; this reads MerchantUser.MerchantId directly, the former separate assignment lookup is
/// absorbed), an unknown role code is 400, and an in-merchant Active target's assignments are set to exactly the
/// requested set. Role CRUD (create/update/delete) moved to <c>Iam.Application.Roles</c> (rf2) —
/// <c>Iam.Tests.CreateRoleHandlerTests</c>/<c>UpdateRoleHandlerTests</c>/<c>DeleteRoleHandlerTests</c> cover those
/// guards on the unified handlers now.
/// </summary>
public sealed class MerchantUserRoleHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid MerchantA = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private static readonly Guid MerchantB = Guid.Parse("b0000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Actor = Guid.Parse("ac000000-0000-0000-0000-0000000000c1");

    [Fact]
    public async Task SetUserRoles_hides_a_target_outside_the_acting_merchant()
    {
        var users = new FakeUsers();
        var target = Approved(MerchantB, users); // different merchant
        var roles = new FakeRoles();
        roles.SeedRole("merchant_member");
        var handler = new SetRolesHandler(users, roles, new FakeUow(), new FakeClock());

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(
            new SetRolesCommand(target.Id, ["merchant_member"], MerchantA, Actor), default).AsTask());
    }

    [Fact]
    public async Task SetUserRoles_rejects_an_unknown_role_code()
    {
        var users = new FakeUsers();
        var target = Approved(MerchantA, users);
        var handler = new SetRolesHandler(users, new FakeRoles(), new FakeUow(), new FakeClock());

        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(
            new SetRolesCommand(target.Id, ["ghost_role"], MerchantA, Actor), default).AsTask());
    }

    [Fact]
    public async Task SetUserRoles_sets_an_in_merchant_target_to_exactly_the_requested_roles()
    {
        var users = new FakeUsers();
        var target = Approved(MerchantA, users);
        var roles = new FakeRoles();
        var member = roles.SeedRole("merchant_member");
        var finance = roles.SeedRole("finance");
        // pre-existing assignment to `member`; request only `finance` -> add finance, remove member.
        roles.Assignments.Add(RoleAssignment.Create(target.Id, member, MerchantA, Actor, Now));
        var handler = new SetRolesHandler(users, roles, new FakeUow(), new FakeClock());

        await handler.Handle(new SetRolesCommand(target.Id, ["finance"], MerchantA, Actor), default);

        var roleIds = roles.Assignments.Where(a => a.MerchantUserId == target.Id).Select(a => a.RoleId).ToHashSet();
        Assert.Equal(new HashSet<Guid> { finance }, roleIds);
        Assert.All(roles.Assignments.Where(a => a.MerchantUserId == target.Id), a => Assert.Equal(MerchantA, a.MerchantId));
        Assert.All(roles.Assignments.Where(a => a.MerchantUserId == target.Id), a => Assert.Equal(Actor, a.AssignedById));
    }

    private static User Approved(Guid merchantId, FakeUsers users)
    {
        var a = User.Register(Guid.NewGuid().ToString(), "p@org.com", Now);
        a.Approve(merchantId, Now);
        users.Seed(a);
        return a;
    }

    private sealed class FakeUow : IUserUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken ct) => op(ct);
    }

    private sealed class FakeClock : IClock { public DateTime UtcNow => Now; }

    private sealed class FakeUsers : IUserRepository
    {
        private readonly Dictionary<Guid, User> _byId = [];
        public void Seed(User u) => _byId[u.Id] = u;
        public Task<User?> FindByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(_byId.GetValueOrDefault(id));
        public Task<User?> FindBySubjectAsync(string subject, CancellationToken ct) => throw new NotSupportedException();
        public void Add(User account) => throw new NotSupportedException();
    }

    private sealed class FakeRoles : IRoleRepository
    {
        private readonly Dictionary<string, Guid> _byCode = [];
        public readonly List<RoleAssignment> Assignments = [];

        /// <summary>A role visible/active in the acting merchant's set — id-only, since role CRUD/catalog
        /// moved to <c>Iam.Application.Roles.IRoleStore</c> (rf2); this fake only resolves codes to ids.</summary>
        public Guid SeedRole(string code)
        {
            var id = Guid.NewGuid();
            _byCode[code] = id;
            return id;
        }

        public void AddAssignment(RoleAssignment assignment) => Assignments.Add(assignment);
        public void RemoveAssignment(RoleAssignment assignment) => Assignments.Remove(assignment);

        public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
            Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(
                _byCode.Where(kv => codes.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
        public Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(Assignments.Where(a => a.MerchantUserId == merchantUserId).Select(a => a.RoleId).ToHashSet());
        public Task<RoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken ct) =>
            Task.FromResult(Assignments.FirstOrDefault(a => a.MerchantUserId == merchantUserId && a.RoleId == roleId));

        // Unused by the handlers under test.
        public Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(
            Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid merchantUserId, Guid merchantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid merchantUserId, Guid merchantId, CancellationToken ct) => throw new NotSupportedException();
    }
}
