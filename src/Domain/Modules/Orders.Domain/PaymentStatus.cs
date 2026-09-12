namespace Orders.Domain;

/// <summary>สถานะเงินของ Order แยกจาก lifecycle ของ Order และ Transaction ของ Task 6.</summary>
public enum PaymentStatus
{
    Unpaid = 1,
    Processing = 2,
    Paid = 3,
}
