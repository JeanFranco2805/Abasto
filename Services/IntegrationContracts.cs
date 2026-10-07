namespace Abasto.Services;

public sealed record ReceiptLine(string Description, decimal Quantity, string Unit, decimal UnitPrice, decimal Total);
public sealed record ReceiptData(string StoreName, string TicketNumber, DateTime CreatedAt, string Cashier,
    IReadOnlyCollection<ReceiptLine> Lines, decimal Subtotal, decimal Tax, decimal Total, decimal Change);

public interface IReceiptPrinter
{
    Task PrintAsync(ReceiptData receipt, CancellationToken cancellationToken = default);
}

public interface ICashDrawer
{
    Task OpenAsync(CancellationToken cancellationToken = default);
}

public interface IScale
{
    Task<decimal> ReadWeightAsync(CancellationToken cancellationToken = default);
}

public interface IPaymentTerminal
{
    Task<string> ChargeAsync(decimal amount, CancellationToken cancellationToken = default);
    Task RefundAsync(string paymentReference, decimal amount, CancellationToken cancellationToken = default);
}

public interface IElectronicInvoicingProvider
{
    Task<string> IssueInvoiceAsync(long saleId, CancellationToken cancellationToken = default);
    Task<string> IssueCreditNoteAsync(long saleId, decimal amount, CancellationToken cancellationToken = default);
}

public interface IBackOfficeSyncClient
{
    Task SynchronizePendingOperationsAsync(CancellationToken cancellationToken = default);
}
