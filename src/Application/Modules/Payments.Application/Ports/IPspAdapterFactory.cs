using Payments.Domain.Psp;

namespace Payments.Application.Ports;

/// <summary>Resolves the <see cref="IPspAdapter"/> for a given <see cref="Code"/>.</summary>
public interface IPspAdapterFactory
{
    /// <summary>Returns the adapter for <paramref name="psp"/>. Throws if none is registered.</summary>
    IPspAdapter For(Code psp);
}
