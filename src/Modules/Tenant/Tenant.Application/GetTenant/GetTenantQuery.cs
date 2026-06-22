using Mediator;

namespace Tenant.Application.GetTenant;

/// <summary>Admin read-back of a provisioned tenant by code. NOT ITenantScoped (admin cross-tenant).</summary>
public sealed record GetTenantQuery(string Code) : IQuery<TenantView>;
