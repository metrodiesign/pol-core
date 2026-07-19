namespace BuildingBlocks.Application;

/// <summary>
/// The 11 denial/rollback/authz event kinds rls-to-query-filter REQ-13.1 names, in the requirement's own
/// order. Each has exactly one or a small handful of throw/catch sites in the codebase — see the
/// <see cref="ISecurityTelemetry"/> call sites for where every category is actually emitted.
/// </summary>
public enum DenialCategory
{
    GuardDenial,
    CanWriteDenial,
    ConcurrencyConflict,
    CheckOrForeignKeyViolation,
    UnboundActor,
    EmptyOrSentinelHit,
    PortCardinalityAnomaly,
    ApplockTimeout,
    AdminCrossMerchantAction,
    AdminRevalidationDenial,
    RegistrationSentinelMisuse,
}

/// <summary>
/// One security-relevant denial/rollback/authz event (REQ-13.1/13.2). <see cref="Reason"/> MUST be a short,
/// fixed, developer-authored literal — never <c>exception.Message</c>, never user input, never anything
/// string-interpolated with a value that could carry PII or a secret (REQ-13.2's "ห้าม log PII/secret" is
/// enforced at the call site by this discipline, checked mechanically by
/// <c>Architecture.Tests.SecurityTelemetryRedactionTests</c> — every <see cref="ISecurityTelemetry.Emit"/>
/// call site in <c>src/</c> must pass <see cref="Reason"/> a compile-time string literal, never an
/// interpolated `$"..."`). <see cref="ActorId"/>/<see cref="TargetMerchant"/> are identifiers, not PII —
/// safe to carry as-is.
/// </summary>
public sealed record DenialEvent(
    DenialCategory Category,
    string ActorKind,
    Guid? ActorId,
    Guid? TargetMerchant,
    string Entity,
    string Operation,
    string Reason,
    string CorrelationId,
    DateTime OccurredAt);

/// <summary>
/// Durable non-blocking sink for <see cref="DenialEvent"/>s (REQ-13.4) — <see cref="Emit"/> never throws and
/// never blocks the caller's write/read path on network I/O; the concrete host-registered implementation
/// owns buffering/retry/delivery to the external tamper-resistant sink.
/// </summary>
public interface ISecurityTelemetry
{
    void Emit(DenialEvent evt);
}

/// <summary>For design-time tooling (<c>dotnet ef migrations</c>) — it only builds the model, never executes
/// a write, so there is nothing to emit. Also the SQLite in-memory unit-test contexts, which assert the
/// write floor's exception directly and have no sink to verify against.</summary>
public sealed class NoOpSecurityTelemetry : ISecurityTelemetry
{
    public static readonly NoOpSecurityTelemetry Instance = new();

    public void Emit(DenialEvent evt) { }
}
