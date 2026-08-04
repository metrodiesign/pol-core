namespace Products.Infrastructure.Sp;

/// <summary>
/// Non-secret configuration for the VCentralPay document-search procedures, bound from the
/// <c>SpDocument</c> section. It lives in Infrastructure — not in <c>Products.Application</c> — because the
/// Application layer has no options package and must not gain one; the adapter is the only thing that reads
/// it (precedent: <c>Payments.Infrastructure/Psp/PspOptions.cs</c>).
/// <para>Both connection strings are nullable because REQ-5.7 below decides what happens when they are
/// unset, not this class — but neither has a derive/fallback anymore (external-sim-separate-containers
/// supersedes products-sp-gateway REQ-3.4): hippodb/mammothdb each run on their own SQL Server instance
/// now, so there is no single "app connection, different InitialCatalog" left to re-point. Every
/// environment sets these two values explicitly. Cutover to the real motordb/centerdb is a config
/// override of these two values and nothing else.</para>
/// <para>Deliberately NOT validated with <c>.ValidateOnStart()</c> (REQ-5.7): 17 hosts boot for real in
/// Hosts.Tests, and a startup validation would take every one of them down over a dependency that is only
/// touched when a search request arrives.</para>
/// </summary>
public sealed class SpDocumentOptions
{
    public const string SectionName = "SpDocument";

    /// <summary>The <c>@BranchCode</c> every call sends. Server-side per §1.1 — never accepted from the
    /// client, which is why it is absent from <c>SpDocumentSearchRequest</c>. The procedures only validate
    /// it (blank or NULL is error 50004) and never filter on it, so this interim constant is safe; the
    /// eventual source is the actor's branch claim.</summary>
    public string BranchCode { get; set; } = "000";

    public string? MotorConnectionString { get; set; }

    public string? NonMotorConnectionString { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 15;
}
