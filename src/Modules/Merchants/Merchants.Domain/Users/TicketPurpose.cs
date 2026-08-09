namespace Merchants.Domain.Users;

/// <summary>Why a registration wire ticket was issued. A first-time applicant gets a
/// <see cref="Registration"/> ticket; a rejected applicant gets a <see cref="Correction"/> ticket to resubmit.
/// Carried in the signed wire-ticket payload.</summary>
public enum TicketPurpose
{
    Registration = 1,
    Correction = 2,
}
