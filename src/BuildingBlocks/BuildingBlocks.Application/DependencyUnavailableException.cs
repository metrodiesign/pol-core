namespace BuildingBlocks.Application;

/// <summary>A read against OUR OWN platform database (VCentralPay) could not produce a usable answer —
/// connection, TLS/pre-login, timeout, login or permission failure. The promise is scoped to the FAILED
/// OPERATION: that read had no side effect. It says nothing about writes the same request may have
/// committed earlier, and it must never be thrown for a write whose outcome is unknown (same warning as
/// UpstreamUnavailableException).</summary>
public sealed class DependencyUnavailableException : Exception
{
    public DependencyUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
