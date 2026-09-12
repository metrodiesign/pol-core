namespace Payments.Domain;

/// <summary>Financial lifecycle of one redirect-only payment attempt.</summary>
public enum TransactionStatus
{
    Created = 1,
    PendingConfirmation = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
    Expired = 6,
}
