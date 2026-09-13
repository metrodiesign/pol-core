using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Access.Domain;
using Admins.Application.Users;
using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;
using Iam.Application.Roles;
using Iam.Domain.Roles;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Iam;

namespace Persistence.ControlPlane.IdentityAccess;

/// <summary>
/// Control-plane store for the target Account/Access model. Employee JIT uses a serializable transaction and the
/// unique identity index, so concurrent first logins produce one Account/Employee/LoginAccount set. The legacy
/// Admins/Merchants stores remain available until the later cutover task and are never consulted as an email merge.
/// </summary>
internal sealed class IdentityAccessStore
    : IEmployeeJitStore, IRegistrationSessionStore, IAssertionReplayStore,
      IBffSessionStore, IRegistrationSessionLookup, IIdentityAccessQuery, IIdentityAccessAdminStore
{
    private readonly ControlPlaneDbContext db;
    private readonly IClock clock;
    private readonly IUnitOfWork? unitOfWork;
    private readonly IAdminOperationStore? operations;
    private readonly IRoleAssignmentValidator? roleAssignments;
    private readonly IOpenIddictApplicationManager? openIddictApplications;
    private readonly IOpenIddictScopeManager? openIddictScopes;
    private readonly IIdentityAuthorizationRoleReader? roleReader;

    public IdentityAccessStore(
        ControlPlaneDbContext db, IClock clock, IRoleAssignmentValidator? roleAssignments = null,
        IIdentityAuthorizationRoleReader? roleReader = null)
    {
        this.db = db;
        this.clock = clock;
        this.roleAssignments = roleAssignments;
        this.roleReader = roleReader;
    }

    public IdentityAccessStore(
        ControlPlaneDbContext db,
        IClock clock,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IAdminOperationStore operations,
        IRoleAssignmentValidator? roleAssignments = null,
        IOpenIddictApplicationManager? openIddictApplications = null,
        IOpenIddictScopeManager? openIddictScopes = null,
        IIdentityAuthorizationRoleReader? roleReader = null)
    {
        this.db = db;
        this.clock = clock;
        this.unitOfWork = unitOfWork;
        this.operations = operations;
        this.roleAssignments = roleAssignments;
        this.openIddictApplications = openIddictApplications;
        this.openIddictScopes = openIddictScopes;
        this.roleReader = roleReader;
    }
    public async Task<EmployeeJitResult> GetOrCreateAsync(
        VerifiedHumanIdentity identity, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await GetOrCreateOnceAsync(identity, cancellationToken);
            }
            catch (Exception exception) when (IsDeadlock(exception) && attempt < 2)
            {
                // SQL Server can deadlock two SERIALIZABLE first-login writers while they take the
                // unique identity index and primary-key locks in opposite order. Retry the whole
                // transaction so the winner is resolved by the unique identity row.
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private async Task<EmployeeJitResult> GetOrCreateOnceAsync(
        VerifiedHumanIdentity identity, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var login = await db.LoginAccounts.SingleOrDefaultAsync(
            x => x.Provider == identity.Identity.Provider
                && x.TenantId == identity.Identity.TenantId
                && x.ExternalUserId == identity.Identity.ExternalUserId,
            cancellationToken);

        if (login is not null)
        {
            var existing = await db.Accounts.SingleAsync(x => x.Id == login.AccountId, cancellationToken);
            if (existing.AccountType != AccountType.Employee)
                throw new IdentityAccessException("identity_account_type_conflict", "The identity is linked to another account type.");
            if (existing.Status != AccountStatus.Active)
                throw new IdentityAccessException("account_suspended", "The Employee account is suspended.");

            login.Observe(identity.Email, identity.DisplayName, clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new EmployeeJitResult(existing.Id, false, false, false, existing.AuthorizationVersion);
        }

        var displayName = string.IsNullOrWhiteSpace(identity.DisplayName)
            ? identity.Email ?? "Employee"
            : identity.DisplayName;
        var account = Account.Create(AccountType.Employee, displayName, clock.UtcNow);
        db.Accounts.Add(account);
        db.LoginAccounts.Add(LoginAccount.Create(
            account.Id, identity.Identity, identity.Email, identity.DisplayName, clock.UtcNow));
        db.Employees.Add(Employee.Create(account.Id, employeeCode: null, departmentCode: null));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new EmployeeJitResult(account.Id, true, false, false, account.AuthorizationVersion);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            if (!await IdentityExistsAfterRaceAsync(identity, cancellationToken))
                throw;
            var winner = await db.LoginAccounts.SingleAsync(
                x => x.Provider == identity.Identity.Provider
                    && x.TenantId == identity.Identity.TenantId
                    && x.ExternalUserId == identity.Identity.ExternalUserId,
                cancellationToken);
            var winnerAccount = await db.Accounts.SingleAsync(x => x.Id == winner.AccountId, cancellationToken);
            if (winnerAccount.AccountType != AccountType.Employee || winnerAccount.Status != AccountStatus.Active)
                throw new IdentityAccessException("identity_account_type_conflict", "The identity is linked to another account type.");
            return new EmployeeJitResult(winnerAccount.Id, false, false, false, winnerAccount.AuthorizationVersion);
        }
    }

    private static bool IsDeadlock(Exception exception) =>
        exception is SqlException { Number: 1205 }
        || exception.InnerException is not null && IsDeadlock(exception.InnerException);

    public Task<bool> HasApprovedAccountAsync(
        ExternalIdentity identity, CancellationToken cancellationToken) =>
        db.LoginAccounts.AnyAsync(
            x => x.Provider == identity.Provider
                && x.TenantId == identity.TenantId
                && x.ExternalUserId == identity.ExternalUserId,
            cancellationToken);

    public async Task<RegistrationSession> IssueAsync(
        ExternalIdentity identity, Guid merchantId, byte[] sessionReferenceHash, DateTime now, TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var session = RegistrationSession.Issue(
            sessionReferenceHash, identity, merchantId, now, lifetime);
        db.RegistrationSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<bool> TryClaimAsync(
        string applicationId, string jti, DateTime expiresAt, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var replay = AssertionReplay.Create(applicationId, jti, expiresAt, now);
        db.AssertionReplays.Add(replay);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await ReplayExistsAsync(applicationId, jti, cancellationToken))
                throw;
            return false;
        }
    }

    private async Task<bool> IdentityExistsAfterRaceAsync(
        VerifiedHumanIdentity identity, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        return await db.LoginAccounts.AnyAsync(
            x => x.Provider == identity.Identity.Provider
                && x.TenantId == identity.Identity.TenantId
                && x.ExternalUserId == identity.Identity.ExternalUserId,
            cancellationToken);
    }

    private async Task<bool> ReplayExistsAsync(
        string applicationId, string jti, CancellationToken cancellationToken) =>
        await db.AssertionReplays.AnyAsync(
            x => x.ApplicationId == applicationId && x.Jti == jti, cancellationToken);

    Task<BffSessionTicket?> IBffSessionStore.FindByHashAsync(
        byte[] ticketKeyHash, CancellationToken cancellationToken) =>
        db.BffSessionTickets.FirstOrDefaultAsync(x => x.TicketKeyHash == ticketKeyHash, cancellationToken);

    public void Add(BffSessionTicket ticket) => db.BffSessionTickets.Add(ticket);

    public async Task RevokeAsync(
        BffSessionTicket ticket, DateTime now, CancellationToken cancellationToken)
    {
        ticket.Revoke(now);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task ReplaceAsync(
        BffSessionTicket current, BffSessionTicket replacement, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        current.Revoke(now);
        db.BffSessionTickets.Add(replacement);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    Task<RegistrationSession?> IRegistrationSessionLookup.FindByHashAsync(
        byte[] sessionReferenceHash, CancellationToken cancellationToken) =>
        db.RegistrationSessions.FirstOrDefaultAsync(
            x => x.SessionReferenceHash == sessionReferenceHash, cancellationToken);

    public Task<Account?> FindAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);

    public async Task<SystemClientResolution?> FindSystemClientAsync(
        string clientId, CancellationToken cancellationToken)
    {
        var client = await db.SystemClients.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientId == clientId, cancellationToken);
        if (client is null)
            return null;
        var account = await db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == client.AccountId, cancellationToken);
        if (account is null)
            return null;
        var keys = await db.ClientKeyPolicies.AsNoTracking()
            .Where(x => x.SystemClientId == client.Id)
            .OrderBy(x => x.KeyId)
            .ToListAsync(cancellationToken);
        var scopes = await db.SystemClientScopes.AsNoTracking()
            .Where(x => x.SystemClientId == client.Id)
            .OrderBy(x => x.ScopeCode)
            .Select(x => x.ScopeCode)
            .ToListAsync(cancellationToken);
        return new SystemClientResolution(account, client, keys, scopes);
    }

    public async Task<AuthorizationSnapshot?> ResolveAuthorizationAsync(
        Guid accountId, Guid? merchantId, Guid? clientId, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account is null)
            return null;

        var platformAccess = account.AccountType == AccountType.Employee
            ? await db.PlatformAccess.AsNoTracking()
                .FirstOrDefaultAsync(x => x.EmployeeAccountId == accountId, cancellationToken)
            : null;
        var platformRoleIds = platformAccess is null
            ? new HashSet<Guid>()
            : await db.PlatformAccessRoles.AsNoTracking()
                .Where(x => x.PlatformAccessId == platformAccess.Id)
                .Select(x => x.RoleId)
                .ToHashSetAsync(cancellationToken);

        Access.Domain.MerchantAccess? access = null;
        var branchIds = new HashSet<Guid>();
        var merchantRoleIds = new HashSet<Guid>();
        if (merchantId is { } selectedMerchant)
        {
            access = await db.AccountMerchantAccess.AsNoTracking().FirstOrDefaultAsync(
                x => x.AccountId == accountId
                    && x.MerchantId == selectedMerchant
                    && x.Status == Access.Domain.AccessStatus.Active,
                cancellationToken);
            if (access is not null)
            {
                branchIds = await db.BranchAccess.AsNoTracking()
                    .Where(x => x.MerchantAccessId == access.Id)
                    .Select(x => x.BranchId)
                    .ToHashSetAsync(cancellationToken);
                merchantRoleIds = await db.AccessRoles.AsNoTracking()
                    .Where(x => x.MerchantAccessId == access.Id)
                    .Select(x => x.RoleId)
                    .ToHashSetAsync(cancellationToken);
            }
        }

        var roleIds = platformRoleIds.Concat(merchantRoleIds).ToHashSet();
        var permissions = roleIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : roleReader is null
                ? throw new InvalidOperationException("IAM authorization role reader is not configured.")
                : (await roleReader.ResolveEffectivePermissionsAsync(roleIds, cancellationToken))
                    .ToHashSet(StringComparer.Ordinal);
        var agentSaleId = account.AccountType == AccountType.Agent
            ? await db.Agents.AsNoTracking().Where(x => x.AccountId == accountId)
                .Select(x => (Guid?)x.SaleId).FirstOrDefaultAsync(cancellationToken)
            : null;

        return new AuthorizationSnapshot(
            account.Id,
            account.AccountType,
            account.Status,
            account.AuthorizationVersion,
            access?.MerchantId,
            access?.DataScope,
            agentSaleId,
            HomeBranchId: account.AccountType == AccountType.Employee
                && access?.DataScope == Access.Domain.DataScope.Branch
                && branchIds.Count == 1
                ? branchIds.Single()
                : null,
            branchIds,
            roleIds,
            platformAccess?.Status == Access.Domain.PlatformAccessStatus.Active,
            permissions,
            clientId);
    }

    public async Task<IReadOnlyList<MerchantAccessSummary>> ListMerchantAccessAsync(
        Guid accountId, CancellationToken cancellationToken)
    {
        var accesses = await db.AccountMerchantAccess.AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .OrderBy(x => x.MerchantId)
            .ToListAsync(cancellationToken);
        var result = new List<MerchantAccessSummary>(accesses.Count);
        foreach (var access in accesses)
        {
            var roleIds = await db.AccessRoles.AsNoTracking()
                .Where(x => x.MerchantAccessId == access.Id)
                .Select(x => x.RoleId)
                .ToHashSetAsync(cancellationToken);
            var branchIds = await db.BranchAccess.AsNoTracking()
                .Where(x => x.MerchantAccessId == access.Id)
                .Select(x => x.BranchId)
                .ToHashSetAsync(cancellationToken);
            result.Add(new MerchantAccessSummary(
                access.Id, access.MerchantId, access.DataScope, access.Status, roleIds, branchIds));
        }
        return result;
    }

    public async Task<PagedResult<AccountAdminView>> ListAccountsAsync(
        PagedQuery query, CancellationToken cancellationToken)
    {
        var source = db.Accounts.AsNoTracking().OrderBy(x => x.Id);
        var total = await source.LongCountAsync(cancellationToken);
        var accounts = await source.Skip((query.Page - 1) * query.Limit).Take(query.Limit)
            .ToListAsync(cancellationToken);
        var ids = accounts.Select(x => x.Id).ToHashSet();
        var logins = await db.LoginAccounts.AsNoTracking().Where(x => ids.Contains(x.AccountId))
            .ToDictionaryAsync(x => x.AccountId, cancellationToken);
        var clients = await db.SystemClients.AsNoTracking().Where(x => ids.Contains(x.AccountId))
            .ToDictionaryAsync(x => x.AccountId, cancellationToken);
        return new PagedResult<AccountAdminView>(
            accounts.Select(x => ToAccountView(x, logins.GetValueOrDefault(x.Id), clients.GetValueOrDefault(x.Id)))
                .ToArray(), query.Page, query.Limit, total);
    }

    public async Task<AccountAdminView?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account is null)
            return null;
        var login = await db.LoginAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
        var client = await db.SystemClients.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
        return ToAccountView(account, login, client);
    }

    public async Task<AccountAdminView> UpdateAccountAsync(
        AccountAdminUpdate update, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == update.AccountId, cancellationToken)
            ?? throw new NotFoundException("Account was not found.");
        if (account.AuthorizationVersion != update.ExpectedAuthorizationVersion)
            throw new ConcurrencyConflictException("Account authorization version is stale.");
        account.Rename(update.DisplayName, clock.UtcNow);
        if (update.Status == AccountStatus.Suspended)
            account.Suspend(clock.UtcNow);
        else
            account.Reactivate(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        var login = await db.LoginAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id, cancellationToken);
        var client = await db.SystemClients.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id, cancellationToken);
        return ToAccountView(account, login, client);
    }

    public Task<(AccountAdminView Value, bool Replayed)> UpdateAccountIdempotentAsync(
        Guid actorId, string idempotencyKey, AccountAdminUpdate update, CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(actorId, "account.update", idempotencyKey, update,
            ct => UpdateAccountAsync(update, ct), cancellationToken);

    public async Task RevokeSessionsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new NotFoundException("Account was not found.");
        account.BumpAuthorizationVersion(clock.UtcNow);
        var now = clock.UtcNow;
        var sessions = await db.BffSessionTickets.Where(x => x.AccountId == accountId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
            session.Revoke(now);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> RevokeSessionsIdempotentAsync(
        Guid actorId, string idempotencyKey, Guid accountId, CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(actorId, "account.session-revoke", idempotencyKey, new { accountId }, async ct =>
        {
            await RevokeSessionsAsync(accountId, ct);
            return true;
        }, cancellationToken).ContinueWith(x => x.Result.Value, cancellationToken);

    public async Task<IReadOnlyList<BffSessionAdminView>> ListBffSessionsAsync(
        Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new NotFoundException("Account was not found.");
        var now = clock.UtcNow;
        return (await db.BffSessionTickets.AsNoTracking().Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.IssuedAt).ThenByDescending(x => x.Id).ToListAsync(cancellationToken))
            .Select(x => new BffSessionAdminView(x.Id, x.AccountId, x.ClientId, x.IssuedAt, x.ExpiresAt,
                x.RevokedAt, x.IsLiveAt(now, account.AuthorizationVersion))).ToArray();
    }

    public async Task RevokeBffSessionAsync(Guid accountId, Guid sessionId, CancellationToken cancellationToken)
    {
        var row = await db.BffSessionTickets.SingleOrDefaultAsync(
            x => x.Id == sessionId && x.AccountId == accountId, cancellationToken)
            ?? throw new NotFoundException("Session was not found.");
        row.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SystemClientAdminView>> ListSystemClientsAsync(
        Guid? merchantId, CancellationToken cancellationToken)
    {
        var clients = db.SystemClients.AsNoTracking().AsQueryable();
        if (merchantId is { } selected)
            clients = clients.Where(x => x.MerchantId == selected);
        var rows = await clients.OrderBy(x => x.ClientId).ToListAsync(cancellationToken);
        return await Task.WhenAll(rows.Select(x => ToSystemClientViewAsync(x, cancellationToken)));
    }

    public async Task<SystemClientAdminView?> GetSystemClientAsync(Guid systemClientId, CancellationToken cancellationToken)
    {
        var client = await db.SystemClients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == systemClientId, cancellationToken);
        return client is null ? null : await ToSystemClientViewAsync(client, cancellationToken);
    }

    public async Task<SystemClientAdminView> CreateSystemClientAsync(
        SystemClientAdminCreate create, CancellationToken cancellationToken)
    {
        if (await db.SystemClients.AnyAsync(x => x.ClientId == create.ClientId, cancellationToken))
            throw new ConflictException("System client already exists.", "client_id_exists");
        if (openIddictApplications is not null
            && await openIddictApplications.FindByClientIdAsync(create.ClientId, cancellationToken) is not null)
            throw new ConflictException("The OAuth application already exists.", "application_id_exists");
        ValidateSystemScopes(create.Scopes);
        var account = Account.Create(AccountType.System, create.DisplayName, clock.UtcNow);
        var client = SystemClient.Create(account.Id, create.ClientId, create.MerchantId,
            create.Environment, ["client_credentials"], clock.UtcNow);
        db.Accounts.Add(account);
        db.SystemClients.Add(client);
        foreach (var scope in create.Scopes.Distinct(StringComparer.Ordinal))
            db.SystemClientScopes.Add(Access.Domain.SystemClientScope.Create(client.Id, scope));
        await SyncOpenIddictApplicationAsync(client, create.Scopes, addedJwkJson: null, removedKid: null,
            displayNameOverride: create.DisplayName, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ToSystemClientViewAsync(client, cancellationToken);
    }

    public Task<(SystemClientAdminView Value, bool Replayed)> CreateSystemClientIdempotentAsync(
        Guid actorId, string idempotencyKey, SystemClientAdminCreate create, CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(actorId, "system-client.create", idempotencyKey, create,
            ct => CreateSystemClientAsync(create, ct), cancellationToken);

    public async Task<SystemClientAdminView> UpdateSystemClientAsync(
        SystemClientAdminUpdate update, CancellationToken cancellationToken)
    {
        var client = await db.SystemClients.SingleOrDefaultAsync(x => x.Id == update.SystemClientId, cancellationToken)
            ?? throw new NotFoundException("System client was not found.");
        var account = await db.Accounts.SingleAsync(x => x.Id == client.AccountId, cancellationToken);
        if (account.AuthorizationVersion != update.ExpectedAuthorizationVersion)
            throw new ConcurrencyConflictException("System client version is stale.");
        account.Rename(update.DisplayName, clock.UtcNow);
        if (update.Status == SystemClientStatus.Suspended)
            client.Suspend(clock.UtcNow);
        else
            client.Reactivate(clock.UtcNow);
        account.BumpAuthorizationVersion(clock.UtcNow);
        await SyncOpenIddictApplicationAsync(client, scopesOverride: null, addedJwkJson: null, removedKid: null,
            displayNameOverride: update.DisplayName, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ToSystemClientViewAsync(client, cancellationToken);
    }

    public Task<(SystemClientAdminView Value, bool Replayed)> UpdateSystemClientIdempotentAsync(
        Guid actorId, string idempotencyKey, SystemClientAdminUpdate update, CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(actorId, "system-client.update", idempotencyKey, update,
            ct => UpdateSystemClientAsync(update, ct), cancellationToken);

    public async Task<SystemClientAdminView> ReplaceSystemClientAccessAsync(
        SystemClientAccessReplace replace, CancellationToken cancellationToken)
    {
        ValidateSystemScopes(replace.Scopes);
        var client = await db.SystemClients.SingleOrDefaultAsync(x => x.Id == replace.SystemClientId, cancellationToken)
            ?? throw new NotFoundException("System client was not found.");
        var existing = await db.SystemClientScopes.Where(x => x.SystemClientId == client.Id).ToListAsync(cancellationToken);
        db.SystemClientScopes.RemoveRange(existing);
        foreach (var scope in replace.Scopes.Distinct(StringComparer.Ordinal))
            db.SystemClientScopes.Add(Access.Domain.SystemClientScope.Create(client.Id, scope));
        var account = await db.Accounts.SingleAsync(x => x.Id == client.AccountId, cancellationToken);
        account.BumpAuthorizationVersion(clock.UtcNow);
        await SyncOpenIddictApplicationAsync(client, replace.Scopes, addedJwkJson: null, removedKid: null,
            displayNameOverride: null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ToSystemClientViewAsync(client, cancellationToken);
    }

    public Task<(SystemClientAdminView Value, bool Replayed)> ReplaceSystemClientAccessIdempotentAsync(
        Guid actorId, string idempotencyKey, SystemClientAccessReplace replace, CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(actorId, "system-client.access", idempotencyKey, replace,
            ct => ReplaceSystemClientAccessAsync(replace, ct), cancellationToken);

    public async Task<IReadOnlyList<ClientKeyAdminView>> ListClientKeysAsync(
        Guid systemClientId, CancellationToken cancellationToken) =>
        (await db.ClientKeyPolicies.AsNoTracking().Where(x => x.SystemClientId == systemClientId)
            .OrderBy(x => x.KeyId).ToListAsync(cancellationToken)).Select(ToKeyView).ToArray();

    public async Task<ClientKeyAdminView> CreateClientKeyAsync(
        ClientKeyAdminCreate create, CancellationToken cancellationToken)
    {
        var client = await db.SystemClients.SingleOrDefaultAsync(
            x => x.Id == create.SystemClientId, cancellationToken);
        if (client is null)
            throw new NotFoundException("System client was not found.");
        if (!string.Equals(client.ClientId, create.ApplicationId, StringComparison.Ordinal))
            throw new ConflictException(
                "The key application does not match the System client.", "application_mismatch");
        var publicJwkSetJson = NormalizePublicJwkSet(create.PublicJwkJson);
        ValidatePublicJwk(publicJwkSetJson, create.Kid, create.Algorithm);
        if (await db.ClientKeyPolicies.AnyAsync(x => x.SystemClientId == create.SystemClientId && x.KeyId == create.Kid, cancellationToken))
            throw new ConflictException("Client key already exists.", "key_id_exists");
        var row = ClientKeyPolicy.Create(create.SystemClientId, create.ApplicationId, create.Kid,
            create.Algorithm, create.ValidFrom, create.ValidUntil, create.AuditReference);
        db.ClientKeyPolicies.Add(row);
        await SyncOpenIddictApplicationAsync(client, scopesOverride: null, publicJwkSetJson, removedKid: null,
            displayNameOverride: null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToKeyView(row);
    }

    public Task<(ClientKeyAdminView Value, bool Replayed)> CreateClientKeyIdempotentAsync(
        Guid actorId, string idempotencyKey, ClientKeyAdminCreate create, CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(actorId, "system-client.key-create", idempotencyKey, create,
            ct => CreateClientKeyAsync(create, ct), cancellationToken);

    public async Task DeleteClientKeyAsync(Guid systemClientId, Guid keyId, CancellationToken cancellationToken)
    {
        var row = await db.ClientKeyPolicies.SingleOrDefaultAsync(
            x => x.SystemClientId == systemClientId && x.Id == keyId, cancellationToken)
            ?? throw new NotFoundException("Client key was not found.");
        row.Revoke();
        var client = await db.SystemClients.SingleAsync(x => x.Id == systemClientId, cancellationToken);
        await SyncOpenIddictApplicationAsync(client, scopesOverride: null, addedJwkJson: null,
            removedKid: row.KeyId, displayNameOverride: null, cancellationToken);
        var account = await db.Accounts.SingleAsync(x => x.Id == client.AccountId, cancellationToken);
        account.BumpAuthorizationVersion(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteClientKeyIdempotentAsync(
        Guid actorId, string idempotencyKey, Guid systemClientId, Guid keyId, CancellationToken cancellationToken)
    {
        var result = await ExecuteIdempotentAsync(actorId, "system-client.key-revoke", idempotencyKey,
            new { systemClientId, keyId }, async ct =>
            {
                await DeleteClientKeyAsync(systemClientId, keyId, ct);
                return true;
            }, cancellationToken);
        return result.Value;
    }

    private async Task SyncOpenIddictApplicationAsync(
        SystemClient client,
        IReadOnlyList<string>? scopesOverride,
        string? addedJwkJson,
        string? removedKid,
        string? displayNameOverride,
        CancellationToken cancellationToken)
    {
        if (openIddictApplications is null)
            throw new DependencyUnavailableException(
                "OpenIddict application provisioning is not configured.",
                new InvalidOperationException("IOpenIddictApplicationManager is unavailable."));

        var scopes = (scopesOverride ?? await db.SystemClientScopes.AsNoTracking()
                .Where(x => x.SystemClientId == client.Id)
                .Select(x => x.ScopeCode)
                .ToListAsync(cancellationToken))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        ValidateSystemScopes(scopes);

        if (openIddictScopes is null)
            throw new DependencyUnavailableException(
                "OpenIddict scope provisioning is not configured.",
                new InvalidOperationException("IOpenIddictScopeManager is unavailable."));
        foreach (var scope in scopes)
        {
            if (await openIddictScopes.FindByNameAsync(scope, cancellationToken) is null)
                await openIddictScopes.CreateAsync(new OpenIddictScopeDescriptor
                {
                    Name = scope,
                    DisplayName = scope,
                    Description = $"Registered SystemClient scope {scope}",
                }, cancellationToken);
        }

        var activeKeyIds = (await db.ClientKeyPolicies.AsNoTracking()
            .Where(x => x.SystemClientId == client.Id && x.Status == KeyPolicyStatus.Active)
            .Select(x => x.KeyId)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (removedKid is not null)
            activeKeyIds.Remove(removedKid);

        var application = await openIddictApplications.FindByClientIdAsync(
            client.ClientId, cancellationToken);
        if (application is null && addedJwkJson is null)
            return;
        if (application is not null && activeKeyIds.Count == 0 && addedJwkJson is null)
        {
            await openIddictApplications.DeleteAsync(application, cancellationToken);
            return;
        }
        var keys = new Dictionary<string, JsonWebKey>(StringComparer.Ordinal);
        if (application is not null)
        {
            var existing = await openIddictApplications.GetJsonWebKeySetAsync(application, cancellationToken);
            foreach (var key in existing?.Keys ?? [])
            {
                if (!string.IsNullOrWhiteSpace(key.Kid) && activeKeyIds.Contains(key.Kid))
                    keys[key.Kid] = key;
            }
        }

        if (addedJwkJson is not null)
        {
            var incoming = new JsonWebKeySet(addedJwkJson).Keys.Single();
            keys[incoming.Kid!] = incoming;
        }

        var keySet = new JsonWebKeySet();
        foreach (var key in keys.Values.OrderBy(x => x.Kid, StringComparer.Ordinal))
            keySet.Keys.Add(key);
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ApplicationType = OpenIddictConstants.ApplicationTypes.Web,
            ClientId = client.ClientId,
            ClientType = OpenIddictConstants.ClientTypes.Confidential,
            ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
            DisplayName = displayNameOverride ?? (application is null
                ? client.ClientId
                : await openIddictApplications.GetDisplayNameAsync(application, cancellationToken) ?? client.ClientId),
            JsonWebKeySet = keySet,
            ClientSecret = null,
        };
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Resource + SystemClientScopeRegistry.ApiAudience);
        foreach (var scope in scopes)
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + scope);

        if (application is null)
            await openIddictApplications.CreateAsync(descriptor, cancellationToken);
        else
            await openIddictApplications.UpdateAsync(application, descriptor, cancellationToken);
    }

    private static void ValidatePublicJwk(string json, string kid, string algorithm)
    {
        JsonWebKeySet set;
        try
        {
            set = new JsonWebKeySet(json);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidRequestException("The public JWK is invalid.", "invalid_public_jwk");
        }

        var privateMaterial = false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var key = document.RootElement.GetProperty("keys").EnumerateArray().Single();
            privateMaterial = key.EnumerateObject().Any(property =>
                property.Name is "d" or "p" or "q" or "dp" or "dq" or "qi" or "k");
        }
        catch (Exception) when (!string.IsNullOrWhiteSpace(json))
        {
            privateMaterial = true;
        }
        if (set.Keys.Count != 1 || !string.Equals(set.Keys[0].Kid, kid, StringComparison.Ordinal)
            || !string.Equals(set.Keys[0].Kty, "RSA", StringComparison.Ordinal)
            || !string.Equals(set.Keys[0].Alg, algorithm, StringComparison.Ordinal)
            || set.Keys[0].D is not null || privateMaterial)
            throw new InvalidRequestException("The public JWK does not match the key metadata.", "invalid_public_jwk");
    }

    private static void ValidateSystemScopes(IEnumerable<string> scopes)
    {
        var values = scopes.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Any(x => !SystemClientScopeRegistry.IsRegistered(x)))
            throw new InvalidRequestException("The requested SYSTEM scope is not registered.", "invalid_scope");
    }

    private static string NormalizePublicJwkSet(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("keys", out _))
            return json;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            keys = new[] { document.RootElement.Clone() },
        });
    }

    public async Task<MerchantAccessAdminView?> GetMerchantAccessAsync(
        Guid accountId, Guid merchantId, CancellationToken cancellationToken)
    {
        var access = await db.AccountMerchantAccess.AsNoTracking().SingleOrDefaultAsync(
            x => x.AccountId == accountId && x.MerchantId == merchantId, cancellationToken);
        return access is null ? null : await ToMerchantAccessViewAsync(access, cancellationToken);
    }

    public async Task<MerchantAccessAdminView> ReplaceMerchantAccessAsync(
        MerchantAccessReplace replace, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == replace.AccountId, cancellationToken)
            ?? throw new NotFoundException("Account was not found.");
        var requestedRoles = replace.RoleIds.Distinct().ToArray();
        var roles = await ResolveRoleTargetsAsync(requestedRoles, cancellationToken);
        if (roles.Count != requestedRoles.Length
            || roles.Any(x => x.Scope == global::Iam.Domain.Permissions.Scope.Platform
                || x.MerchantId is not null && x.MerchantId != replace.MerchantId))
            throw new InvalidRequestException("Role is outside the target Merchant scope.", "cross_merchant_role");
        foreach (var branchId in replace.BranchIds.Distinct())
        {
            var owner = await db.Database.SqlQueryRaw<Guid>(
                    "SELECT TOP(1) [Id] AS [Value] FROM [merch].[Branches] WHERE [Id] = {0} AND [MerchantId] = {1}",
                    branchId, replace.MerchantId)
                .FirstOrDefaultAsync(cancellationToken);
            if (owner != branchId)
                throw new InvalidRequestException("Branch is outside the target Merchant scope.", "cross_merchant_branch");
        }
        var access = await db.AccountMerchantAccess.SingleOrDefaultAsync(
            x => x.AccountId == replace.AccountId && x.MerchantId == replace.MerchantId, cancellationToken);
        if (access is null)
        {
            access = Access.Domain.MerchantAccess.Create(replace.AccountId, replace.MerchantId, replace.DataScope);
            db.AccountMerchantAccess.Add(access);
        }
        else
        {
            if (access.Version != replace.ExpectedVersion)
                throw new ConcurrencyConflictException("Merchant access version is stale.");
            access.ReplaceScope(replace.DataScope);
            access.Activate();
            db.AccessRoles.RemoveRange(db.AccessRoles.Where(x => x.MerchantAccessId == access.Id));
            db.BranchAccess.RemoveRange(db.BranchAccess.Where(x => x.MerchantAccessId == access.Id));
            db.MerchantAccessMethods.RemoveRange(db.MerchantAccessMethods.Where(x => x.MerchantAccessId == access.Id));
        }
        foreach (var role in requestedRoles)
            db.AccessRoles.Add(Access.Domain.AccessRole.Create(access.Id, replace.MerchantId, role));
        foreach (var branch in replace.BranchIds.Distinct())
            db.BranchAccess.Add(Access.Domain.BranchAccess.Create(access.Id, replace.MerchantId, branch));
        foreach (var method in replace.PaymentMethods.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
            db.MerchantAccessMethods.Add(Access.Domain.MerchantAccessMethod.Create(access.Id, method));
        account.BumpAuthorizationVersion(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await ToMerchantAccessViewAsync(access, cancellationToken);
    }

    public Task<(MerchantAccessAdminView Value, bool Replayed)> ReplaceMerchantAccessIdempotentAsync(
        Guid actorId, string idempotencyKey, MerchantAccessReplace replace, CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(actorId, "merchant-access.replace", idempotencyKey, replace,
            ct => ReplaceMerchantAccessAsync(replace, ct), cancellationToken);

    public async Task RevokeMerchantAccessAsync(
        Guid accountId, Guid merchantId, long expectedVersion, CancellationToken cancellationToken)
    {
        var access = await db.AccountMerchantAccess.SingleOrDefaultAsync(
            x => x.AccountId == accountId && x.MerchantId == merchantId, cancellationToken)
            ?? throw new NotFoundException("Merchant access was not found.");
        if (access.Version != expectedVersion)
            throw new ConcurrencyConflictException("Merchant access version is stale.");
        access.Revoke();
        var account = await db.Accounts.SingleAsync(x => x.Id == accountId, cancellationToken);
        account.BumpAuthorizationVersion(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> RevokeMerchantAccessIdempotentAsync(
        Guid actorId, string idempotencyKey, Guid accountId, Guid merchantId, long expectedVersion,
        CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(actorId, "merchant-access.revoke", idempotencyKey,
            new { accountId, merchantId, expectedVersion }, async ct =>
            {
                await RevokeMerchantAccessAsync(accountId, merchantId, expectedVersion, ct);
                return true;
            }, cancellationToken).ContinueWith(x => x.Result.Value, cancellationToken);

    public async Task<PlatformAccessAdminView?> GetPlatformAccessAsync(
        Guid accountId, CancellationToken cancellationToken)
    {
        var row = await db.PlatformAccess.AsNoTracking().SingleOrDefaultAsync(
            x => x.EmployeeAccountId == accountId, cancellationToken);
        return row is null ? null : await ToPlatformAccessViewAsync(row, cancellationToken);
    }

    public async Task<PlatformAccessAdminView> ReplacePlatformAccessAsync(
        PlatformAccessReplace replace, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == replace.AccountId, cancellationToken)
            ?? throw new NotFoundException("Account was not found.");
        if (account.AccountType != AccountType.Employee)
            throw new InvalidRequestException("Platform access requires an Employee account.", "invalid_target");
        var requestedRoles = replace.RoleIds.Distinct().ToArray();
        var roles = await ResolveRoleTargetsAsync(requestedRoles, cancellationToken);
        if (roles.Count != requestedRoles.Length
            || roles.Any(x => x.Scope == global::Iam.Domain.Permissions.Scope.Merchant || x.MerchantId is not null))
            throw new InvalidRequestException("Role is outside the Platform scope.", "cross_merchant_role");
        var row = await db.PlatformAccess.SingleOrDefaultAsync(x => x.EmployeeAccountId == replace.AccountId, cancellationToken);
        if (row is null)
        {
            row = Access.Domain.PlatformAccess.Create(replace.AccountId);
            db.PlatformAccess.Add(row);
        }
        else
        {
            if (row.Version != replace.ExpectedVersion)
                throw new ConcurrencyConflictException("Platform access version is stale.");
            if (replace.Status == PlatformAccessStatus.Revoked) row.Revoke(); else row.Activate();
            db.PlatformAccessRoles.RemoveRange(db.PlatformAccessRoles.Where(x => x.PlatformAccessId == row.Id));
        }
        foreach (var role in requestedRoles)
            db.PlatformAccessRoles.Add(Access.Domain.PlatformAccessRole.Create(row.Id, role, roleScope: 1));
        account.BumpAuthorizationVersion(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await ToPlatformAccessViewAsync(row, cancellationToken);
    }

    public Task<(PlatformAccessAdminView Value, bool Replayed)> ReplacePlatformAccessIdempotentAsync(
        Guid actorId, string idempotencyKey, PlatformAccessReplace replace, CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(actorId, "platform-access.replace", idempotencyKey, replace,
            ct => ReplacePlatformAccessAsync(replace, ct), cancellationToken);

    private async Task<(T Value, bool Replayed)> ExecuteIdempotentAsync<T>(
        Guid actorId,
        string operation,
        string idempotencyKey,
        object intent,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new InvalidRequestException("Idempotency-Key is invalid.", "validation_failed");
        var transaction = unitOfWork ?? throw new InvalidOperationException("Idempotency transaction is not configured.");
        var idempotency = operations ?? throw new InvalidOperationException("Idempotency store is not configured.");
        return await transaction.ExecuteInTransactionAsync(async ct =>
        {
            await idempotency.AcquireAsync(actorId, operation, idempotencyKey, ct);
            var requestHash = Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent))).ToLowerInvariant();
            var prior = await idempotency.FindReplayAsync(actorId, operation, idempotencyKey, ct);
            if (prior is not null)
            {
                if (!string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal))
                    throw new ConflictException("Idempotency key was reused with a different intent.", "idempotency_key_reused");
                if (prior.InProgress || prior.ResponseBody is null)
                    throw new ConflictException("The operation is still in progress or has an unknown outcome.", "operation_in_progress");
                return (JsonSerializer.Deserialize<T>(prior.ResponseBody)
                    ?? throw new InvalidOperationException("Stored operation result is invalid."), true);
            }
            var value = await action(ct);
            idempotency.AddSucceeded(actorId, operation, idempotencyKey, requestHash,
                200, JsonSerializer.Serialize(value), clock.UtcNow, clock.UtcNow.AddHours(24));
            await unitOfWork.SaveChangesAsync(ct);
            return (value, false);
        }, cancellationToken);
    }

    private Task<IReadOnlyList<RoleAssignmentTarget>> ResolveRoleTargetsAsync(
        IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken) =>
        roleAssignments?.ResolveTargetsAsync(roleIds, cancellationToken)
        ?? throw new InvalidOperationException("IAM role assignment validation is not configured.");

    private async Task<SystemClientAdminView> ToSystemClientViewAsync(
        SystemClient client, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == client.AccountId, cancellationToken);
        var scopes = await db.SystemClientScopes.AsNoTracking().Where(x => x.SystemClientId == client.Id)
            .OrderBy(x => x.ScopeCode).Select(x => x.ScopeCode).ToListAsync(cancellationToken);
        var keys = await db.ClientKeyPolicies.AsNoTracking().Where(x => x.SystemClientId == client.Id)
            .OrderBy(x => x.KeyId).ToListAsync(cancellationToken);
        return new SystemClientAdminView(client.Id, client.AccountId, client.ClientId, client.MerchantId,
            client.Environment, client.Status, account.AuthorizationVersion, scopes, keys.Select(ToKeyView).ToArray());
    }

    private async Task<MerchantAccessAdminView> ToMerchantAccessViewAsync(
        Access.Domain.MerchantAccess access, CancellationToken cancellationToken)
    {
        var roles = await db.AccessRoles.AsNoTracking().Where(x => x.MerchantAccessId == access.Id)
            .Select(x => x.RoleId).ToListAsync(cancellationToken);
        var branches = await db.BranchAccess.AsNoTracking().Where(x => x.MerchantAccessId == access.Id)
            .Select(x => x.BranchId).ToListAsync(cancellationToken);
        var methods = await db.MerchantAccessMethods.AsNoTracking().Where(x => x.MerchantAccessId == access.Id)
            .Select(x => x.MethodCode).ToListAsync(cancellationToken);
        return new MerchantAccessAdminView(access.Id, access.AccountId, access.MerchantId, access.DataScope,
            access.Status, access.Version, roles, branches, methods);
    }

    private async Task<PlatformAccessAdminView> ToPlatformAccessViewAsync(
        Access.Domain.PlatformAccess access, CancellationToken cancellationToken)
    {
        var roles = await db.PlatformAccessRoles.AsNoTracking().Where(x => x.PlatformAccessId == access.Id)
            .Select(x => x.RoleId).ToListAsync(cancellationToken);
        return new PlatformAccessAdminView(access.EmployeeAccountId, access.Status, access.Version, roles);
    }

    private static AccountAdminView ToAccountView(
        Account account, LoginAccount? login, SystemClient? client) =>
        new(account.Id, account.AccountType, account.DisplayName, account.Status, account.AuthorizationVersion,
            login?.Provider, login?.TenantId, login?.ExternalUserId, client?.MerchantId, client?.Id);

    private static ClientKeyAdminView ToKeyView(ClientKeyPolicy key) =>
        new(key.Id, key.ApplicationId, key.KeyId, key.Algorithm, key.ValidFrom, key.ValidUntil, key.Status);
}

internal static class RegistrationReferenceHash
{
    public static byte[] Compute(string rawReference) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(rawReference));
}
