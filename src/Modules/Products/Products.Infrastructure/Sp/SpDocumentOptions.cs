namespace Products.Infrastructure.Sp;

/// <summary>
/// Non-secret configuration for the VCentralPay document-search procedures, bound from the
/// <c>SpDocument</c> section. It lives in Infrastructure — not in <c>Products.Application</c> — because the
/// Application layer has no options package and must not gain one; the adapter is the only thing that reads
/// it (precedent: <c>Payments.Infrastructure/Psp/PspOptions.cs</c>).
/// <para>Both connection strings are nullable on purpose: when either is left unset the host fills it in
/// (<c>PostConfigure</c> in Program.cs) by re-pointing the app connection at the simulated catalogue on the
/// same instance, so no environment gains a new variable (REQ-3.4). Cutover to the real motordb/centerdb is
/// then a config override of these two values and nothing else.</para>
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
