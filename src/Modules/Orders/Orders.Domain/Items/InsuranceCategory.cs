namespace Orders.Domain.Items;

/// <summary>Insurance category recorded on an <see cref="ItemPolicy"/> (REQ-1.2) — ภาคสมัครใจ
/// (<see cref="Voluntary"/>) or ภาคบังคับ/พ.ร.บ. (<see cref="Compulsory"/>). Generic across every insurance
/// type (REQ-1.8) — Motor is only a use case, not a constraint baked into this vocabulary.</summary>
public enum InsuranceCategory
{
    Voluntary = 0,
    Compulsory = 1,
}
