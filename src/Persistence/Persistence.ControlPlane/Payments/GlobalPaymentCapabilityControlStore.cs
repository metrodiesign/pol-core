using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Persistence.ControlPlane.Governance;

namespace Persistence.ControlPlane.Payments;

internal sealed class GlobalPaymentCapabilityControlStore(
    ControlPlaneDbContext db,
    IClock clock,
    IPspAdapterFactory adapters,
    ControlPlaneOperationExecutor operations,
    PaymentAuthorizationSqlLockManager locks) : IGlobalPaymentCapabilityControlStore
{
    public async Task<GlobalPaymentCapabilityView?> GetMethodAsync(
        string method, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        EnsureUnrestricted(access);
        var code = PaymentMethods.Normalize(method);
        var row = await db.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(x => x.Code == code, cancellationToken);
        return row is null ? null : MethodView(row);
    }

    public async Task<GlobalPaymentCapabilityView?> GetProviderAsync(
        string provider, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        EnsureUnrestricted(access);
        var code = NormalizeProvider(provider);
        var row = await db.PaymentProviders.AsNoTracking().SingleOrDefaultAsync(x => x.Code == code, cancellationToken);
        return row is null ? null : ProviderView(row);
    }

    public async Task<GlobalPaymentCapabilityView?> GetProviderMethodAsync(
        string provider, string method, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        EnsureUnrestricted(access);
        var context = await LoadProviderMethodContextAsync(provider, method, tracking: false, cancellationToken);
        if (context is null)
            return null;
        return ProviderMethodView(context.Value.Provider, context.Value.Method, context.Value.ProviderMethod);
    }

    public async Task<GlobalPaymentCapabilityView?> GetProviderMethodOptionAsync(
        string provider, string method, string option, AdminPaymentsAccess access,
        CancellationToken cancellationToken)
    {
        EnsureUnrestricted(access);
        var context = await LoadProviderMethodContextAsync(provider, method, tracking: false, cancellationToken);
        if (context is null || context.Value.ProviderMethod is null)
            return null;
        var optionCode = NormalizeOption(option);
        var canonical = await db.PaymentMethodOptions.AsNoTracking().SingleOrDefaultAsync(
            x => x.PaymentMethodId == context.Value.Method.Id && x.Code == optionCode, cancellationToken);
        if (canonical is null)
            return null;
        var row = await db.PaymentProviderMethodOptions.AsNoTracking().SingleOrDefaultAsync(
            x => x.PaymentProviderMethodId == context.Value.ProviderMethod.Id
                 && x.PaymentMethodOptionId == canonical.Id, cancellationToken);
        return ProviderOptionView(context.Value.Provider, context.Value.Method,
            context.Value.ProviderMethod, canonical, row);
    }

    public Task<PaymentCapabilityMutationResult<GlobalPaymentCapabilityView>> SetMethodAsync(
        SetGlobalPaymentCapabilityIntent intent, CancellationToken cancellationToken)
    {
        EnsureUnrestricted(intent.Access);
        var method = PaymentMethods.Normalize(intent.Code);
        return ExecuteAsync("payment.method.set", intent, async ct =>
        {
            var row = await db.PaymentMethods.SingleOrDefaultAsync(x => x.Code == method, ct)
                ?? throw new NotFoundException("Payment method was not found.");
            EnsureVersion(row.Version, intent.ExpectedVersion);
            row.SetActive(intent.Enabled, intent.Access.ActorId, clock.UtcNow);
            return MethodView(row);
        }, cancellationToken);
    }

    public Task<PaymentCapabilityMutationResult<GlobalPaymentCapabilityView>> SetProviderAsync(
        SetGlobalPaymentCapabilityIntent intent, CancellationToken cancellationToken)
    {
        EnsureUnrestricted(intent.Access);
        var provider = NormalizeProvider(intent.Code);
        return ExecuteAsync("payment.provider.set", intent, async ct =>
        {
            var row = await db.PaymentProviders.SingleOrDefaultAsync(x => x.Code == provider, ct)
                ?? throw new NotFoundException("Payment provider was not found.");
            EnsureVersion(row.Version, intent.ExpectedVersion);
            row.SetEnabled(intent.Enabled, intent.Access.ActorId, clock.UtcNow);
            return ProviderView(row);
        }, cancellationToken);
    }

    public Task<PaymentCapabilityMutationResult<GlobalPaymentCapabilityView>> SetProviderMethodAsync(
        SetGlobalPaymentCapabilityIntent intent, CancellationToken cancellationToken)
    {
        EnsureUnrestricted(intent.Access);
        var provider = NormalizeProvider(intent.Provider ?? string.Empty);
        var method = PaymentMethods.Normalize(intent.Method ?? intent.Code);
        return ExecuteAsync("payment.provider-method.set", intent, async ct =>
        {
            var context = await LoadProviderMethodContextAsync(provider, method, tracking: true, ct)
                ?? throw new NotFoundException("Payment provider or method was not found.");
            var row = context.ProviderMethod;
            EnsureVersion(row?.Version ?? 0, intent.ExpectedVersion);
            var supported = AdapterSupports(context.Provider, context.Method.Code);
            if (intent.Enabled && (!context.Provider.IsEnabled || !context.Method.IsActive || !supported))
                throw new PaymentCapabilityUnavailableException(
                    "Provider method exceeds an inactive parent or adapter capability.");
            if (row is null)
            {
                if (!intent.Enabled)
                    return ProviderMethodView(context.Provider, context.Method, null);
                row = PaymentProviderMethod.Create(
                    context.Provider.Id, context.Method.Id, intent.Access.ActorId, clock.UtcNow);
                db.PaymentProviderMethods.Add(row);
            }
            else
            {
                row.SetActive(intent.Enabled, intent.Access.ActorId, clock.UtcNow);
            }
            return ProviderMethodView(context.Provider, context.Method, row);
        }, cancellationToken);
    }

    public Task<PaymentCapabilityMutationResult<GlobalPaymentCapabilityView>> SetProviderMethodOptionAsync(
        SetGlobalPaymentCapabilityIntent intent, CancellationToken cancellationToken)
    {
        EnsureUnrestricted(intent.Access);
        var provider = NormalizeProvider(intent.Provider ?? string.Empty);
        var method = PaymentMethods.Normalize(intent.Method ?? string.Empty);
        var option = NormalizeOption(intent.Option ?? intent.Code);
        return ExecuteAsync("payment.provider-method-option.set", intent, async ct =>
        {
            var context = await LoadProviderMethodContextAsync(provider, method, tracking: true, ct)
                ?? throw new NotFoundException("Payment provider or method was not found.");
            if (context.ProviderMethod is null)
                throw new PaymentCapabilityUnavailableException("Provider method is not configured.");
            var canonical = await db.PaymentMethodOptions.SingleOrDefaultAsync(
                x => x.PaymentMethodId == context.Method.Id && x.Code == option, ct)
                ?? throw new NotFoundException("Payment method option was not found.");
            var row = await db.PaymentProviderMethodOptions.SingleOrDefaultAsync(
                x => x.PaymentProviderMethodId == context.ProviderMethod.Id
                     && x.PaymentMethodOptionId == canonical.Id, ct);
            EnsureVersion(row?.Version ?? 0, intent.ExpectedVersion);
            var supported = AdapterSupports(context.Provider, context.Method.Code);
            if (intent.Enabled && (!context.Provider.IsEnabled || !context.Method.IsActive
                || !context.ProviderMethod.IsActive || !supported))
                throw new PaymentCapabilityUnavailableException(
                    "Provider option exceeds an inactive parent or adapter capability.");
            if (row is null)
            {
                if (!intent.Enabled)
                    return ProviderOptionView(context.Provider, context.Method,
                        context.ProviderMethod, canonical, null);
                row = PaymentProviderMethodOption.Create(
                    context.ProviderMethod.Id, context.Method.Id, canonical.Id,
                    intent.Access.ActorId, clock.UtcNow);
                db.PaymentProviderMethodOptions.Add(row);
            }
            else
            {
                row.SetActive(intent.Enabled, intent.Access.ActorId, clock.UtcNow);
            }
            return ProviderOptionView(context.Provider, context.Method,
                context.ProviderMethod, canonical, row);
        }, cancellationToken);
    }

    private async Task<PaymentCapabilityMutationResult<GlobalPaymentCapabilityView>> ExecuteAsync(
        string operation, SetGlobalPaymentCapabilityIntent intent,
        Func<CancellationToken, Task<GlobalPaymentCapabilityView>> action,
        CancellationToken cancellationToken)
    {
        var result = await operations.ExecutePlatformAsync(
            intent.Access.ActorId, operation, intent.IdempotencyKey,
            new
            {
                intent.Code, intent.Provider, intent.Method, intent.Option,
                intent.Enabled, intent.ExpectedVersion,
            }, 200,
            locks.AcquireGlobalExclusiveAsync, action, cancellationToken);
        return new PaymentCapabilityMutationResult<GlobalPaymentCapabilityView>(result.Value, result.Replayed);
    }

    private async Task<(PaymentProvider Provider, PaymentMethod Method, PaymentProviderMethod? ProviderMethod)?>
        LoadProviderMethodContextAsync(string provider, string method, bool tracking, CancellationToken ct)
    {
        var providerCode = NormalizeProvider(provider);
        var methodCode = PaymentMethods.Normalize(method);
        var providers = tracking ? db.PaymentProviders : db.PaymentProviders.AsNoTracking();
        var methods = tracking ? db.PaymentMethods : db.PaymentMethods.AsNoTracking();
        var providerRow = await providers.SingleOrDefaultAsync(x => x.Code == providerCode, ct);
        var methodRow = await methods.SingleOrDefaultAsync(x => x.Code == methodCode, ct);
        if (providerRow is null || methodRow is null)
            return null;
        var source = tracking ? db.PaymentProviderMethods : db.PaymentProviderMethods.AsNoTracking();
        var providerMethod = await source.SingleOrDefaultAsync(
            x => x.PaymentProviderId == providerRow.Id && x.PaymentMethodId == methodRow.Id, ct);
        return (providerRow, methodRow, providerMethod);
    }

    private GlobalPaymentCapabilityView ProviderMethodView(
        PaymentProvider provider, PaymentMethod method, PaymentProviderMethod? row) => new(
        "provider-method", method.Code, provider.Code, method.Code, null,
        row?.IsActive == true, AdapterSupports(provider, method.Code),
        row?.UpdatedBy ?? row?.CreatedBy, row?.UpdatedAt ?? row?.CreatedAt, row?.Version ?? 0);

    private GlobalPaymentCapabilityView ProviderOptionView(
        PaymentProvider provider, PaymentMethod method, PaymentProviderMethod providerMethod,
        PaymentMethodOption option, PaymentProviderMethodOption? row) => new(
        "provider-method-option", option.Code, provider.Code, method.Code, option.Code,
        row?.IsActive == true, AdapterSupports(provider, method.Code),
        row?.UpdatedBy ?? row?.CreatedBy, row?.UpdatedAt ?? row?.CreatedAt, row?.Version ?? 0);

    private static GlobalPaymentCapabilityView MethodView(PaymentMethod row) => new(
        "method", row.Code, null, row.Code, null, row.IsActive, true,
        row.UpdatedBy, row.UpdatedAt, row.Version);

    private static GlobalPaymentCapabilityView ProviderView(PaymentProvider row) => new(
        "provider", row.Code, row.Code, null, null, row.IsEnabled, true,
        row.UpdatedBy, row.UpdatedAt, row.Version);

    private bool AdapterSupports(PaymentProvider provider, string method) =>
        adapters.For(provider.AdapterCode).SupportedMethods.Contains(method);

    private static void EnsureUnrestricted(AdminPaymentsAccess access)
    {
        if (!access.IsUnrestricted)
            throw new AdminPaymentsAccessDeniedException("Global payment capability requires unrestricted Admin access.");
    }

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected)
            throw new ConcurrencyConflictException("The resource version is stale.");
    }

    private static string NormalizeProvider(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 32 || normalized.Any(char.IsControl))
            throw new ArgumentException("Payment provider code is invalid.", nameof(value));
        return normalized;
    }

    private static string NormalizeOption(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length > 32 || normalized.Any(char.IsControl))
            throw new ArgumentException("Payment option code is invalid.", nameof(value));
        return normalized;
    }
}
