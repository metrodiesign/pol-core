namespace Admins.Application.Users;

/// <summary>Initializes or verifies the database-wide immutable workforce tenant binding.</summary>
public interface IWorkforceTenantBindingStore
{
    Task EnsureAsync(Guid configuredTenantId, CancellationToken cancellationToken);
}
