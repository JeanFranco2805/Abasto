namespace Backend.Domain;

public sealed class CentralSyncEvent
{
    public long Id { get; set; }
    public string ClientId { get; set; } = "";
    public long LocalEventId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Payload { get; set; } = "";
}

public sealed class CentralProduct
{
    public long Id { get; set; }
    public string ClientId { get; set; } = "";
    public int LocalProductId { get; set; }
    public string Barcode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "UND";
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal Stock { get; set; }
    public decimal MinimumStock { get; set; }
    public bool IsActive { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class CentralSale
{
    public long Id { get; set; }
    public string ClientId { get; set; } = "";
    public long LocalSaleId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Status { get; set; } = "Completada";
    public int CashierId { get; set; }
    public string CashierName { get; set; } = "";
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string Payload { get; set; } = "{}";
}
