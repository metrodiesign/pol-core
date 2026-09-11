namespace Access.Domain;

public enum DataScope
{
    Merchant = 1,
    Self = 2,
    Branch = 3,
    AssignedBranches = 4,
}

public enum AccessStatus
{
    Active = 1,
    Revoked = 2,
}

/// <summary>One explicit account/merchant context. No row means no merchant access.</summary>
public sealed class MerchantAccess
{
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid MerchantId { get; private set; }
    public DataScope DataScope { get; private set; }
    public AccessStatus Status { get; private set; }
    public long Version { get; private set; }

    private MerchantAccess() { }

    private MerchantAccess(Guid id, Guid accountId, Guid merchantId, DataScope dataScope)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("AccountId is required.", nameof(accountId));
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        if (!Enum.IsDefined(dataScope))
            throw new ArgumentOutOfRangeException(nameof(dataScope));
        Id = id;
        AccountId = accountId;
        MerchantId = merchantId;
        DataScope = dataScope;
        Status = AccessStatus.Active;
        Version = 1;
    }

    public static MerchantAccess Create(Guid accountId, Guid merchantId, DataScope dataScope) =>
        new(Guid.CreateVersion7(), accountId, merchantId, dataScope);

    public void ReplaceScope(DataScope dataScope)
    {
        if (!Enum.IsDefined(dataScope))
            throw new ArgumentOutOfRangeException(nameof(dataScope));
        if (DataScope == dataScope)
            return;
        DataScope = dataScope;
        Version++;
    }

    public void Revoke()
    {
        if (Status == AccessStatus.Revoked)
            return;
        Status = AccessStatus.Revoked;
        Version++;
    }

    public void Activate()
    {
        if (Status == AccessStatus.Active)
            return;
        Status = AccessStatus.Active;
        Version++;
    }
}

public sealed class AccessRole
{
    public Guid Id { get; private set; }
    public Guid MerchantAccessId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid RoleId { get; private set; }

    private AccessRole() { }

    private AccessRole(Guid id, Guid merchantAccessId, Guid merchantId, Guid roleId)
    {
        if (merchantAccessId == Guid.Empty)
            throw new ArgumentException("MerchantAccessId is required.", nameof(merchantAccessId));
        if (roleId == Guid.Empty)
            throw new ArgumentException("RoleId is required.", nameof(roleId));
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        Id = id;
        MerchantAccessId = merchantAccessId;
        MerchantId = merchantId;
        RoleId = roleId;
    }

    public static AccessRole Create(Guid merchantAccessId, Guid merchantId, Guid roleId) =>
        new(Guid.CreateVersion7(), merchantAccessId, merchantId, roleId);

    public static AccessRole Create(Guid merchantAccessId, Guid roleId) =>
        throw new ArgumentException("MerchantId is required for a scoped role assignment.", nameof(merchantAccessId));
}

public sealed class BranchAccess
{
    public Guid Id { get; private set; }
    public Guid MerchantAccessId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid BranchId { get; private set; }

    private BranchAccess() { }

    private BranchAccess(Guid id, Guid merchantAccessId, Guid merchantId, Guid branchId)
    {
        if (merchantAccessId == Guid.Empty)
            throw new ArgumentException("MerchantAccessId is required.", nameof(merchantAccessId));
        if (branchId == Guid.Empty)
            throw new ArgumentException("BranchId is required.", nameof(branchId));
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        Id = id;
        MerchantAccessId = merchantAccessId;
        MerchantId = merchantId;
        BranchId = branchId;
    }

    public static BranchAccess Create(Guid merchantAccessId, Guid merchantId, Guid branchId) =>
        new(Guid.CreateVersion7(), merchantAccessId, merchantId, branchId);

    public static BranchAccess Create(Guid merchantAccessId, Guid branchId) =>
        throw new ArgumentException("MerchantId is required for a scoped branch assignment.", nameof(merchantAccessId));
}

public enum PlatformAccessStatus
{
    Active = 1,
    Revoked = 2,
}

public sealed class PlatformAccess
{
    public Guid Id { get; private set; }
    public Guid EmployeeAccountId { get; private set; }
    public PlatformAccessStatus Status { get; private set; }
    public long Version { get; private set; }

    private PlatformAccess() { }

    private PlatformAccess(Guid id, Guid employeeAccountId)
    {
        if (employeeAccountId == Guid.Empty)
            throw new ArgumentException("EmployeeAccountId is required.", nameof(employeeAccountId));
        Id = id;
        EmployeeAccountId = employeeAccountId;
        Status = PlatformAccessStatus.Active;
        Version = 1;
    }

    public static PlatformAccess Create(Guid employeeAccountId) =>
        new(Guid.CreateVersion7(), employeeAccountId);

    public void Revoke()
    {
        if (Status == PlatformAccessStatus.Revoked)
            return;
        Status = PlatformAccessStatus.Revoked;
        Version++;
    }

    public void Activate()
    {
        if (Status == PlatformAccessStatus.Active)
            return;
        Status = PlatformAccessStatus.Active;
        Version++;
    }
}

public sealed class PlatformAccessRole
{
    public Guid Id { get; private set; }
    public Guid PlatformAccessId { get; private set; }
    public Guid RoleId { get; private set; }
    public int RoleScope { get; private set; }

    private PlatformAccessRole() { }

    private PlatformAccessRole(Guid id, Guid platformAccessId, Guid roleId, int roleScope)
    {
        if (platformAccessId == Guid.Empty)
            throw new ArgumentException("PlatformAccessId is required.", nameof(platformAccessId));
        if (roleId == Guid.Empty)
            throw new ArgumentException("RoleId is required.", nameof(roleId));
        if (roleScope is not (1 or 3))
            throw new ArgumentException("Platform access can only carry Platform or Shared roles.", nameof(roleScope));
        Id = id;
        PlatformAccessId = platformAccessId;
        RoleId = roleId;
        RoleScope = roleScope;
    }

    public static PlatformAccessRole Create(Guid platformAccessId, Guid roleId, int roleScope) =>
        new(Guid.CreateVersion7(), platformAccessId, roleId, roleScope);

    public static PlatformAccessRole Create(Guid platformAccessId, Guid roleId) =>
        throw new ArgumentException("RoleScope is required for a platform role assignment.", nameof(platformAccessId));
}

public sealed class SystemClientScope
{
    public Guid Id { get; private set; }
    public Guid SystemClientId { get; private set; }
    public string ScopeCode { get; private set; } = default!;

    private SystemClientScope() { }

    private SystemClientScope(Guid id, Guid systemClientId, string scopeCode)
    {
        if (systemClientId == Guid.Empty)
            throw new ArgumentException("SystemClientId is required.", nameof(systemClientId));
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeCode);
        ScopeCode = scopeCode.Trim();
        if (ScopeCode.Length > 128)
            throw new ArgumentException("ScopeCode exceeds 128 characters.", nameof(scopeCode));
        Id = id;
        SystemClientId = systemClientId;
    }

    public static SystemClientScope Create(Guid systemClientId, string scopeCode) =>
        new(Guid.CreateVersion7(), systemClientId, scopeCode);
}

public sealed class MerchantAccessMethod
{
    public Guid Id { get; private set; }
    public Guid MerchantAccessId { get; private set; }
    public string MethodCode { get; private set; } = default!;

    private MerchantAccessMethod() { }

    private MerchantAccessMethod(Guid id, Guid merchantAccessId, string methodCode)
    {
        if (merchantAccessId == Guid.Empty)
            throw new ArgumentException("MerchantAccessId is required.", nameof(merchantAccessId));
        ArgumentException.ThrowIfNullOrWhiteSpace(methodCode);
        MethodCode = methodCode.Trim();
        if (MethodCode.Length > 64)
            throw new ArgumentException("MethodCode exceeds 64 characters.", nameof(methodCode));
        Id = id;
        MerchantAccessId = merchantAccessId;
    }

    public static MerchantAccessMethod Create(Guid merchantAccessId, string methodCode) =>
        new(Guid.CreateVersion7(), merchantAccessId, methodCode);
}
