using Microsoft.EntityFrameworkCore.Metadata;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Validates a merchant-scoped entity's tenant-key property at model-build time (rls-to-query-filter
/// REQ-11.7, Codex round-2/round-3 findings). Resolves via <see cref="IMutableEntityType.FindProperty"/> —
/// NOT the fluent <c>Property(x =&gt; x.Y)</c> API, which silently CREATES a shadow property on a typo
/// instead of failing — and rejects a shadow property, the wrong CLR type, or (unless the caller explicitly
/// opts in) a nullable <c>Guid</c>. Call this BEFORE
/// wiring <c>HasQueryFilter</c> so a misconfigured tenant key fails loud the first time the context's model
/// is built, instead of silently becoming a 0-row read floor.
/// <para>
/// Task 3 (write floor, REQ-2.6/REQ-2.9): also configures the property as an EF concurrency token — a
/// forged detached write targets 0 rows and throws <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
/// — and stamps an annotation <see cref="GuardedRuntimeDbContext"/> reads at save time to reject
/// <see cref="Guid.Empty"/> and enforce immutability after insert. Nullable platform-or-merchant rows use
/// <paramref name="allowNullable"/> only; the one pending-user binding flow also opts into
/// <paramref name="allowNullToValue"/>. Keeping those flags separate prevents a platform-scoped Governance
/// row from being retargeted to a merchant through EF's change tracker.
/// </para>
/// </summary>
public static class TenantKeyDescriptor
{
    internal const string AnnotationName = "Pol:TenantKey";
    internal const string AllowNullToValueAnnotationName = "Pol:TenantKeyAllowNullToValue";

    public static void Require(
        IMutableEntityType entityType,
        string propertyName,
        bool allowNullable = false,
        bool allowNullToValue = false)
    {
        var property = entityType.FindProperty(propertyName);
        if (property is null)
            throw new InvalidOperationException(
                $"{entityType.ClrType.Name}: tenant key property '{propertyName}' not found (typo, or it is not a real CLR property).");

        if (property.IsShadowProperty())
            throw new InvalidOperationException(
                $"{entityType.ClrType.Name}: tenant key '{propertyName}' is a shadow property — the tenant key must be a real CLR property.");

        var clrType = property.ClrType;
        var isGuid = clrType == typeof(Guid);
        var isNullableGuid = clrType == typeof(Guid?);

        if (!isGuid && !isNullableGuid)
            throw new InvalidOperationException(
                $"{entityType.ClrType.Name}: tenant key '{propertyName}' must be Guid or Guid?, found {clrType.Name}.");

        if (isNullableGuid && !allowNullable)
            throw new InvalidOperationException(
                $"{entityType.ClrType.Name}: tenant key '{propertyName}' is nullable Guid — pass allowNullable: true explicitly.");

        if (allowNullToValue && (!allowNullable || !isNullableGuid))
            throw new InvalidOperationException(
                $"{entityType.ClrType.Name}: allowNullToValue requires a nullable Guid tenant key.");

        property.IsConcurrencyToken = true;
        entityType.SetAnnotation(AnnotationName, propertyName);
        entityType.SetAnnotation(AllowNullToValueAnnotationName, allowNullToValue);
    }
}
