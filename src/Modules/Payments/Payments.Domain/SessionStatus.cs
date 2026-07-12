namespace Payments.Domain;

/// <summary>
/// Lifecycle of a <see cref="Session"/>. A session is <see cref="Created"/> when bound to an
/// order+amount up-front (PLAN #15), moves to <see cref="Redirected"/> once a hosted PSP charge is
/// attached, and reaches <see cref="Paid"/> only on a PSP-confirmed webhook (the webhook is the
/// source of truth, never the browser return). <see cref="Failed"/> and <see cref="Expired"/> are
/// terminal outcomes for sessions that never complete.
/// </summary>
public enum SessionStatus
{
    Created = 0,
    Redirected = 1,
    Paid = 2,
    Failed = 3,
    Expired = 4,
}
