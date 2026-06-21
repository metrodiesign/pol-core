using BuildingBlocks.Application;
using Mediator;

namespace Payments.Application.StartRedirect;

/// <summary>
/// Creates the hosted PSP charge for a <see cref="Domain.PaymentStatus.Created"/> session and binds it
/// once (PLAN #11), returning the hosted redirect URL the browser is sent to. Tenant-scoped.
/// </summary>
public sealed record StartRedirectCommand(Guid PaymentSessionId)
    : ICommand<StartRedirectResult>, ITenantScoped;

/// <summary>The hosted redirect URL to send the customer's browser to.</summary>
public sealed record StartRedirectResult(string RedirectUrl);
