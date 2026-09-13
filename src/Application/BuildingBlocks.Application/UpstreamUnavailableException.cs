namespace BuildingBlocks.Application;

/// <summary>
/// A dependency this request needed — a system outside our own data plane — could not be reached or did
/// not answer usably: a connection, login or permission failure, a timeout, or a response whose shape no
/// longer matches the contract we were built against. Maps to HTTP 503 with a fixed detail; the actual
/// SQL text, host name or driver message is logged server-side and never reaches the caller.
/// <para>Says nothing about whether the operation happened upstream, so it must not be thrown for a write
/// whose outcome is unknown — this is the exception of a read that produced nothing usable.</para>
/// </summary>
public sealed class UpstreamUnavailableException : Exception
{
    public UpstreamUnavailableException(string message) : base(message) { }

    public UpstreamUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
