using BuildingBlocks.Application;
using Merchants.Domain;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;

namespace Persistence.MerchantRuntime.Payments.Capabilities;

internal sealed class EffectivePaymentCapabilityResolver(
    MerchantRuntimeDbContext db,
    IUnitOfWork unitOfWork,
    IPaymentAuthorizationLockManager locks,
    IPspAdapterFactory adapters) : IEffectivePaymentCapabilityResolver
{
    private static readonly string[] CanonicalMethods =
        [PaymentMethods.Card, PaymentMethods.PromptPay, PaymentMethods.Installment];

    public Task<PaymentMethodDecision> ResolveMethodAsync(
        ResolvePaymentMethod request, CancellationToken cancellationToken)
    {
        ValidateSubject(request.Subject);
        var method = NormalizeMethod(request.Method);
        var provider = NormalizeProvider(request.ProviderCode);
        return ExecuteLockedAsync(request.Subject.MerchantId,
            async ct => await ResolveByModeAsync(
                await ReadModeAsync(ct), request.Subject, method, provider, ct), cancellationToken);
    }

    public Task<IReadOnlyList<EffectivePaymentMethod>> ListMethodsAsync(
        PaymentCapabilitySubject subject, CancellationToken cancellationToken)
    {
        ValidateSubject(subject);
        return ExecuteLockedAsync<IReadOnlyList<EffectivePaymentMethod>>(subject.MerchantId, async ct =>
        {
            var mode = await ReadModeAsync(ct);
            var result = new List<EffectivePaymentMethod>(CanonicalMethods.Length);
            foreach (var method in CanonicalMethods)
            {
                var decision = await ResolveByModeAsync(mode, subject, method, null, ct);
                if (decision.Allowed)
                    result.Add(new EffectivePaymentMethod(method));
            }
            return result;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<EffectivePaymentOption>> ResolveOptionsAsync(
        ResolvePaymentMethod request, CancellationToken cancellationToken)
    {
        ValidateSubject(request.Subject);
        var method = NormalizeMethod(request.Method);
        var provider = NormalizeRequiredProvider(request.ProviderCode);
        return ExecuteLockedAsync<IReadOnlyList<EffectivePaymentOption>>(request.Subject.MerchantId, async ct =>
        {
            var mode = await ReadModeAsync(ct);
            if (mode != PaymentAuthorizationMode.NormalizedRead)
                return [];
            var decision = await ResolveMethodCoreAsync(request.Subject, method, provider, ct);
            if (!decision.Allowed || decision.QualifyingAccountId is null)
                return [];

            var accountMethod = await PlatformReadGuard.ReadAsync(token => db.MerchantProviderAccountMethods
                .IgnoreQueryFilters().AsNoTracking().SingleAsync(x =>
                    x.MerchantId == request.Subject.MerchantId
                    && x.PspConnectionId == decision.QualifyingAccountId
                    && x.PaymentMethodId == MethodId(method) && x.IsEnabled, token), ct);
            var accountOptionsQuery = db.MerchantProviderAccountMethodOptions.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.MerchantId == request.Subject.MerchantId
                    && x.MerchantProviderAccountMethodId == accountMethod.Id && x.IsEnabled)
                .Select(x => new
                {
                    x.PaymentProviderMethodOptionId,
                    x.PaymentMethodOptionId,
                });
            var accountOptions = await PlatformReadGuard.ReadAsync(
                token => accountOptionsQuery.ToListAsync(token), ct);
            if (accountOptions.Count == 0)
                return [];

            var providerOptionsQuery = db.Database.SqlQuery<ProviderOptionRow>($"""
                SELECT pmo.[Id] AS [PaymentProviderMethodOptionId],
                       o.[Id] AS [PaymentMethodOptionId], o.[Code], o.[Name]
                FROM [cfg].[PaymentProviderMethodOptions] pmo
                JOIN [cfg].[PaymentMethodOptions] o
                  ON o.[Id] = pmo.[PaymentMethodOptionId]
                 AND o.[PaymentMethodId] = pmo.[PaymentMethodId]
                WHERE pmo.[PaymentProviderMethodId] = {accountMethod.PaymentProviderMethodId}
                  AND pmo.[PaymentMethodId] = {accountMethod.PaymentMethodId}
                  AND pmo.[IsActive] = CAST(1 AS bit)
                """);
            var providerOptions = await PlatformReadGuard.ReadAsync(
                token => providerOptionsQuery.ToListAsync(token), ct);
            var enabled = accountOptions.Select(x =>
                (x.PaymentProviderMethodOptionId, x.PaymentMethodOptionId)).ToHashSet();
            return providerOptions
                .Where(x => enabled.Contains((x.PaymentProviderMethodOptionId, x.PaymentMethodOptionId)))
                .OrderBy(x => x.Code, StringComparer.Ordinal)
                .Select(x => new EffectivePaymentOption(x.Code, x.Name)).ToList();
        }, cancellationToken);
    }

    private Task<PaymentMethodDecision> ResolveByModeAsync(
        PaymentAuthorizationMode mode,
        PaymentCapabilitySubject subject,
        string method,
        string? providerCode,
        CancellationToken ct) => mode switch
    {
        PaymentAuthorizationMode.LegacyRead => ResolveLegacyAsync(subject, method, providerCode, ct),
        PaymentAuthorizationMode.NormalizedRead => ResolveMethodCoreAsync(subject, method, providerCode, ct),
        PaymentAuthorizationMode.FailClosed => Task.FromResult(
            Denied(method, PaymentCapabilityDenial.MethodUnavailable)),
        _ => Task.FromResult(Denied(method, PaymentCapabilityDenial.MethodUnavailable)),
    };

    private async Task<PaymentAuthorizationMode> ReadModeAsync(CancellationToken ct)
    {
        var query = db.Database.SqlQuery<int>($"""
            SELECT [Mode] AS [Value]
            FROM [cfg].[PaymentAuthorizationStates]
            WHERE [Id] = {PaymentCapabilityIds.AuthorizationState}
            """);
        var value = await PlatformReadGuard.ReadAsync(token => query.SingleOrDefaultAsync(token), ct);
        return Enum.IsDefined(typeof(PaymentAuthorizationMode), value)
            ? (PaymentAuthorizationMode)value
            : PaymentAuthorizationMode.FailClosed;
    }

    private async Task<PaymentMethodDecision> ResolveLegacyAsync(
        PaymentCapabilitySubject subject, string method, string? providerCode, CancellationToken ct)
    {
        var merchant = await PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters()
            .AsNoTracking().SingleOrDefaultAsync(x => x.Id == subject.MerchantId, token), ct);
        if (merchant is null || merchant.Status != MerchantStatus.Active)
            return Denied(method, PaymentCapabilityDenial.MerchantUnavailable);
        if (!LegacyCodes(merchant.EnabledChannels).Contains(method))
            return Denied(method, PaymentCapabilityDenial.MethodUnavailable);

        global::Payments.Domain.Psp.Code? requestedPsp = null;
        if (providerCode is not null)
        {
            try { requestedPsp = global::Payments.Domain.Psp.Codes.FromCode(providerCode); }
            catch (ArgumentOutOfRangeException)
            {
                return Denied(method, PaymentCapabilityDenial.ProviderUnavailable);
            }
        }

        var source = db.PspConnections.IgnoreQueryFilters().AsNoTracking().Where(x =>
            x.MerchantId == subject.MerchantId && x.IsEnabled);
        if (requestedPsp is not null)
            source = source.Where(x => x.Psp == requestedPsp.Value);
        var connections = await PlatformReadGuard.ReadAsync(token => source.OrderBy(x => x.Id).ToListAsync(token), ct);
        foreach (var connection in connections)
        {
            if (LegacyCodes(connection.EnabledMethods).Contains(method)
                && adapters.For(connection.Psp).SupportedMethods.Contains(method))
                return new PaymentMethodDecision(
                    true, method, PaymentCapabilityDenial.None, connection.Id);
        }

        return Denied(method, requestedPsp is null
            ? PaymentCapabilityDenial.AccountUnavailable
            : PaymentCapabilityDenial.ProviderUnavailable);
    }

    private static HashSet<string> LegacyCodes(string? csv) =>
        (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => x.ToLowerInvariant())
        .Where(PaymentMethods.IsKnown)
        .ToHashSet(StringComparer.Ordinal);

    private async Task<PaymentMethodDecision> ResolveMethodCoreAsync(
        PaymentCapabilitySubject subject, string method, string? providerCode, CancellationToken ct)
    {
        if (subject.Audience == PaymentAudience.User)
        {
            var userQuery = db.Database.SqlQuery<UserCapabilityRow>($"""
                SELECT [Id] AS [UserId], [MerchantId], [Status]
                FROM [merch].[Users]
                WHERE [Id] = {subject.MerchantUserId!.Value} AND [MerchantId] = {subject.MerchantId}
                """);
            var user = await PlatformReadGuard.ReadAsync(token => userQuery.SingleOrDefaultAsync(token), ct);
            if (user is null || user.Status != (int)UserStatus.Active)
                return Denied(method, PaymentCapabilityDenial.UserNotActive);
        }

        var merchantActive = await PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters()
            .AsNoTracking().AnyAsync(x => x.Id == subject.MerchantId && x.Status == MerchantStatus.Active, token), ct);
        if (!merchantActive)
            return Denied(method, PaymentCapabilityDenial.MerchantUnavailable);

        var methodQuery = db.Database.SqlQuery<MethodCapabilityRow>($"""
            SELECT [Id] AS [PaymentMethodId], [IsActive]
            FROM [cfg].[PaymentMethods]
            WHERE [Code] = {method}
            """);
        var methodRow = await PlatformReadGuard.ReadAsync(token => methodQuery.SingleOrDefaultAsync(token), ct);
        if (methodRow is null || !methodRow.IsActive)
            return Denied(method, PaymentCapabilityDenial.MethodUnavailable);

        var merchantPolicy = await PlatformReadGuard.ReadAsync(token => db.MerchantPaymentMethods
            .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
                x.MerchantId == subject.MerchantId && x.PaymentMethodId == methodRow.PaymentMethodId, token), ct);
        if (merchantPolicy?.IsEnabled != true)
            return Denied(method, PaymentCapabilityDenial.MethodUnavailable);

        if (subject.Audience == PaymentAudience.User)
        {
            var userPolicy = await PlatformReadGuard.ReadAsync(token => db.MerchantUserPaymentMethods
                .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
                    x.MerchantId == subject.MerchantId
                    && x.MerchantUserId == subject.MerchantUserId
                    && x.PaymentMethodId == methodRow.PaymentMethodId, token), ct);
            if (userPolicy?.IsEnabled != true)
                return Denied(method, PaymentCapabilityDenial.UserPolicyDenied);
        }

        var accountMethods = await PlatformReadGuard.ReadAsync(token => db.MerchantProviderAccountMethods
            .IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.MerchantId == subject.MerchantId
                && x.PaymentMethodId == methodRow.PaymentMethodId && x.IsEnabled)
            .OrderBy(x => x.PspConnectionId).ToListAsync(token), ct);
        if (accountMethods.Count == 0)
            return Denied(method, PaymentCapabilityDenial.AccountUnavailable);

        var sawProvider = false;
        var sawAdapterDrift = false;
        foreach (var accountMethod in accountMethods)
        {
            var connection = await PlatformReadGuard.ReadAsync(token => db.PspConnections.IgnoreQueryFilters()
                .AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountMethod.PspConnectionId
                    && x.MerchantId == subject.MerchantId && x.IsEnabled
                    && x.PaymentProviderId == accountMethod.PaymentProviderId, token), ct);
            if (connection is null)
                continue;
            var chainQuery = db.Database.SqlQuery<ProviderMethodRow>($"""
                SELECT p.[Code] AS [ProviderCode], p.[AdapterCode], p.[IsEnabled] AS [ProviderIsEnabled],
                       pm.[IsActive] AS [ProviderMethodIsActive]
                FROM [cfg].[PaymentProviders] p
                JOIN [cfg].[PaymentProviderMethods] pm
                  ON pm.[PaymentProviderId] = p.[Id]
                WHERE p.[Id] = {accountMethod.PaymentProviderId}
                  AND pm.[Id] = {accountMethod.PaymentProviderMethodId}
                  AND pm.[PaymentMethodId] = {methodRow.PaymentMethodId}
                """);
            var chain = await PlatformReadGuard.ReadAsync(token => chainQuery.SingleOrDefaultAsync(token), ct);
            if (chain is null || providerCode is not null
                && !string.Equals(chain.ProviderCode, providerCode, StringComparison.Ordinal))
                continue;
            sawProvider = true;
            if (!chain.ProviderIsEnabled || !chain.ProviderMethodIsActive)
                continue;
            if (!adapters.For((global::Payments.Domain.Psp.Code)chain.AdapterCode).SupportedMethods.Contains(method))
            {
                sawAdapterDrift = true;
                continue;
            }
            return new PaymentMethodDecision(true, method, PaymentCapabilityDenial.None, connection.Id);
        }

        if (providerCode is not null && !sawProvider)
        {
            var providerQuery = db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM [cfg].[PaymentProviders]
                WHERE [Code] = {providerCode} AND [IsEnabled] = CAST(1 AS bit)
                """);
            var providerExists = await PlatformReadGuard.ReadAsync(token => providerQuery.SingleAsync(token), ct);
            return Denied(method, providerExists == 0
                ? PaymentCapabilityDenial.ProviderUnavailable
                : PaymentCapabilityDenial.AccountUnavailable);
        }
        return Denied(method, sawAdapterDrift
            ? PaymentCapabilityDenial.AdapterUnsupported
            : PaymentCapabilityDenial.AccountUnavailable);
    }

    private Task<T> ExecuteLockedAsync<T>(
        Guid merchantId, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        async Task<T> Locked(CancellationToken token)
        {
            await locks.AcquireMerchantSharedAsync(merchantId, token);
            return await action(token);
        }

        return db.Database.CurrentTransaction is null
            ? unitOfWork.ExecuteInTransactionAsync(Locked, ct)
            : Locked(ct);
    }

    private static PaymentMethodDecision Denied(string method, PaymentCapabilityDenial denial) =>
        new(false, method, denial, null);

    private static void ValidateSubject(PaymentCapabilitySubject subject)
    {
        if (subject.MerchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(subject));
        if (!Enum.IsDefined(subject.Audience)
            || subject.Audience == PaymentAudience.User && subject.MerchantUserId is null
            || subject.Audience == PaymentAudience.PlatformAdmin && subject.MerchantUserId is not null)
            throw new ArgumentException("Payment capability subject is invalid.", nameof(subject));
    }

    private static string NormalizeMethod(string value)
    {
        try { return PaymentMethods.Normalize(value); }
        catch (ArgumentException ex) { throw new InvalidRequestException(ex.Message, "validation_failed"); }
    }

    private static string? NormalizeProvider(string? value)
    {
        if (value is null)
            return null;
        return NormalizeRequiredProvider(value);
    }

    private static string NormalizeRequiredProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidRequestException("Provider code is required.", "validation_failed");
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 32 || normalized.Any(char.IsControl))
            throw new InvalidRequestException("Provider code is invalid.", "validation_failed");
        return normalized;
    }

    private static Guid MethodId(string method) => method switch
    {
        PaymentMethods.Card => PaymentCapabilityIds.Card,
        PaymentMethods.PromptPay => PaymentCapabilityIds.PromptPay,
        PaymentMethods.Installment => PaymentCapabilityIds.Installment,
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private sealed class UserCapabilityRow
    {
        public Guid UserId { get; set; }
        public Guid? MerchantId { get; set; }
        public int Status { get; set; }
    }

    private sealed class MethodCapabilityRow
    {
        public Guid PaymentMethodId { get; set; }
        public bool IsActive { get; set; }
    }

    private sealed class ProviderMethodRow
    {
        public string ProviderCode { get; set; } = default!;
        public int AdapterCode { get; set; }
        public bool ProviderIsEnabled { get; set; }
        public bool ProviderMethodIsActive { get; set; }
    }

    private sealed class ProviderOptionRow
    {
        public Guid PaymentProviderMethodOptionId { get; set; }
        public Guid PaymentMethodOptionId { get; set; }
        public string Code { get; set; } = default!;
        public string Name { get; set; } = default!;
    }
}
