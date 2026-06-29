namespace Producer.Domain;

/// <summary>Why a <see cref="RegistrationTicket"/> was issued (REQ-3.1). A first-time applicant gets a
/// <see cref="Registration"/> ticket; a rejected applicant gets a <see cref="Correction"/> ticket to resubmit
/// (REQ-5.2). Stored as int.</summary>
public enum TicketPurpose
{
    Registration = 0,
    Correction = 1,
}
