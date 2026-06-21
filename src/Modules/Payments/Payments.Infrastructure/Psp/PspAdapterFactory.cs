using Payments.Application.Ports;
using Payments.Domain;

namespace Payments.Infrastructure.Psp;

/// <summary>Resolves the registered <see cref="IPspAdapter"/> for a <see cref="PspCode"/>.</summary>
public sealed class PspAdapterFactory : IPspAdapterFactory
{
    private readonly IReadOnlyDictionary<PspCode, IPspAdapter> _adapters;

    public PspAdapterFactory(IEnumerable<IPspAdapter> adapters) =>
        _adapters = adapters.ToDictionary(a => a.Psp);

    public IPspAdapter For(PspCode psp) =>
        _adapters.TryGetValue(psp, out var adapter)
            ? adapter
            : throw new ArgumentOutOfRangeException(nameof(psp), psp, "No PSP adapter registered.");
}
