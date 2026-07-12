using Payments.Application.Ports;
using Payments.Domain.Psp;

namespace Payments.Infrastructure.Psp;

/// <summary>Resolves the registered <see cref="IPspAdapter"/> for a <see cref="Code"/>.</summary>
public sealed class PspAdapterFactory : IPspAdapterFactory
{
    private readonly IReadOnlyDictionary<Code, IPspAdapter> _adapters;

    public PspAdapterFactory(IEnumerable<IPspAdapter> adapters) =>
        _adapters = adapters.ToDictionary(a => a.Psp);

    public IPspAdapter For(Code psp) =>
        _adapters.TryGetValue(psp, out var adapter)
            ? adapter
            : throw new ArgumentOutOfRangeException(nameof(psp), psp, "No PSP adapter registered.");
}
