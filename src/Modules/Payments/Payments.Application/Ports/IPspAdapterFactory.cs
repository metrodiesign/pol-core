using Payments.Domain;

namespace Payments.Application.Ports;

/// <summary>Resolves the <see cref="IPspAdapter"/> for a given <see cref="PspCode"/>.</summary>
public interface IPspAdapterFactory
{
    /// <summary>Returns the adapter for <paramref name="psp"/>. Throws if none is registered.</summary>
    IPspAdapter For(PspCode psp);
}
