using Payments.Domain.Psp;

namespace Payments.Application.Ports;

/// <summary>Validated, immutable PSP selected once from host configuration.</summary>
public sealed record DefaultPspSelection(Code Psp);
