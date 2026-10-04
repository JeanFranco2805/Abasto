namespace SupermercadoPOS.Domain;

public sealed class Product
{
    public int Id { get; set; }
    public string Barcode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "UND";
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; } = 0.19m;
    public decimal Stock { get; set; }
    public decimal MinimumStock { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PosUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "Cajero";
    public string PinSalt { get; set; } = "";
    public string PinHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class Sale
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Completada";
    public int CashierId { get; set; }
    public string CashierName { get; set; } = "";
    public int? CashShiftId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerDocument { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public bool IsSynced { get; set; }
    public List<SaleItem> Items { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
}

public sealed class SaleItem
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public Sale? Sale { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string Unit { get; set; } = "UND";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public string DiscountApprovedBy { get; set; } = "";
    public decimal DiscountAmount { get; set; }
    public decimal LineSubtotal { get; set; }
    public decimal LineTax { get; set; }
    public decimal LineTotal { get; set; }
}

public sealed class Payment
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public Sale? Sale { get; set; }
    public string Method { get; set; } = "Efectivo";
    public decimal Amount { get; set; }
    public decimal Tendered { get; set; }
    public decimal Change { get; set; }
    public string? ExternalReference { get; set; }
}

public sealed class CashShift
{
    public int Id { get; set; }
    public int CashierId { get; set; }
    public string CashierName { get; set; } = "";
    public DateTime OpenedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAtUtc { get; set; }
    public string Status { get; set; } = "Abierto";
    public decimal OpeningFloat { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal CountedCash { get; set; }
    public decimal Difference { get; set; }
}

public sealed class CashMovement
{
    public long Id { get; set; }
    public int CashShiftId { get; set; }
    public CashShift? CashShift { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public bool IsCashIn { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Details { get; set; } = "";
}

public sealed class SyncQueueItem
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Payload { get; set; } = "";
    public int Attempts { get; set; }
    public string Status { get; set; } = "Pendiente";
}
