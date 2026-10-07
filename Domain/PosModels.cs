namespace Abasto.Domain;

public sealed record SaleLineDraft(
    int ProductId,
    string ProductName,
    string Barcode,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate,
    decimal DiscountRate,
    string DiscountApprovedBy);

public sealed record PaymentDraft(
    string Method,
    decimal Amount,
    decimal Tendered,
    decimal Change);

public sealed record SalesSummary(
    int SaleCount,
    decimal GrossSales,
    decimal CashTotal,
    decimal CardTotal,
    decimal OtherPayments);

public sealed record ProductPerformanceRow(
    string ProductName,
    string Barcode,
    decimal QuantitySold,
    decimal Revenue);

public sealed record PosUserSummary(int Id, string Username, string DisplayName, string Role, bool IsActive);

public sealed record SalesByCashierRow(string CashierName, int SalesCount, decimal GrossSales, decimal CashTotal, decimal OtherPayments);

public sealed record SalesByShiftRow(int ShiftId, string CashierName, int SalesCount, decimal GrossSales);
