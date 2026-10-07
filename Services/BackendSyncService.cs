using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Abasto.Domain;

namespace Abasto.Services;

public sealed record BackendSyncResult(bool IsConfigured, bool IsConnected, int SentCount, int PendingCount, string Message);

public static class BackendSyncService
{
    private static readonly SemaphoreSlim SyncLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<List<Product>?> GetServerProductsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await BackendSettingsService.LoadAsync();
            using var client = CreateReadClient(settings);
            if (client is null)
                return null;

            var url = $"api/products?clientId={Uri.EscapeDataString(settings.ClientId)}";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            var products = await response.Content.ReadFromJsonAsync<List<CentralProductResponse>>(JsonOptions, cancellationToken);
            return products?.Select(product => new Product
            {
                Id = product.LocalProductId,
                Barcode = product.Barcode,
                Name = product.Name,
                Category = product.Category,
                Unit = product.Unit,
                UnitPrice = product.UnitPrice,
                TaxRate = product.TaxRate,
                Stock = product.Stock,
                MinimumStock = product.MinimumStock,
                IsActive = product.IsActive,
                UpdatedAtUtc = product.UpdatedAtUtc
            }).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<List<Sale>?> GetServerSalesAsync(
        DateTime fromLocal, DateTime toLocal, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await BackendSettingsService.LoadAsync();
            using var client = CreateReadClient(settings);
            if (client is null)
                return null;

            var fromUtc = DateTime.SpecifyKind(fromLocal.Date, DateTimeKind.Local).ToUniversalTime();
            var toUtc = DateTime.SpecifyKind(toLocal.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime();
            var url = $"api/sales?clientId={Uri.EscapeDataString(settings.ClientId)}"
                + $"&fromUtc={Uri.EscapeDataString(fromUtc.ToString("O"))}"
                + $"&toUtc={Uri.EscapeDataString(toUtc.ToString("O"))}&take=500";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            var records = await response.Content.ReadFromJsonAsync<List<CentralSaleResponse>>(JsonOptions, cancellationToken);
            if (records is null)
                return null;

            var sales = new List<Sale>(records.Count);
            foreach (var record in records)
            {
                var payload = JsonSerializer.Deserialize<CentralSalePayload>(record.Payload, JsonOptions) ?? new CentralSalePayload();
                var sale = new Sale
                {
                    Id = record.LocalSaleId,
                    CreatedAtUtc = record.CreatedAtUtc,
                    Status = record.Status,
                    CashierId = record.CashierId,
                    CashierName = record.CashierName,
                    CashShiftId = payload.CashShiftId,
                    CustomerName = payload.CustomerName,
                    CustomerDocument = payload.CustomerDocument,
                    Subtotal = record.Subtotal,
                    DiscountTotal = record.DiscountTotal,
                    TaxTotal = record.TaxTotal,
                    Total = record.Total
                };
                sale.Items = payload.Items.Select(item => new SaleItem
                {
                    SaleId = sale.Id,
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    Barcode = item.Barcode,
                    Unit = item.Unit,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    TaxRate = item.TaxRate,
                    DiscountApprovedBy = item.DiscountApprovedBy,
                    DiscountAmount = item.DiscountAmount,
                    LineSubtotal = item.LineSubtotal,
                    LineTax = item.LineTax,
                    LineTotal = item.LineTotal
                }).ToList();
                sale.Payments = payload.Payments.Select(payment => new Payment
                {
                    SaleId = sale.Id,
                    Method = payment.Method,
                    Amount = payment.Amount,
                    Tendered = payment.Tendered,
                    Change = payment.Change
                }).ToList();
                sales.Add(sale);
            }
            return sales;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient? CreateReadClient(BackendSettings settings)
    {
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(settings.ApiKey))
            return null;
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
            BaseAddress = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/")
        };
        client.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);
        return client;
    }

    public static async Task<BackendSyncResult> SynchronizePendingAsync(CancellationToken cancellationToken = default)
    {
        if (!await SyncLock.WaitAsync(0, cancellationToken))
            return new BackendSyncResult(true, false, 0, await PosService.GetPendingSyncCountAsync(), "Sincronización en curso.");

        try
        {
            var settings = await BackendSettingsService.LoadAsync();
            var pendingCount = await PosService.GetPendingSyncCountAsync();
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return new BackendSyncResult(false, false, 0, pendingCount, "Configura la URL y la clave de la API central.");
            }

            using var requestClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15), BaseAddress = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/") };
            requestClient.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);

            using var summaryResponse = await requestClient.GetAsync("api/sync/summary", cancellationToken);
            if (!summaryResponse.IsSuccessStatusCode)
            {
                var message = summaryResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "La API rechazó la clave configurada."
                    : $"La API respondió {(int)summaryResponse.StatusCode} ({summaryResponse.ReasonPhrase}).";
                return new BackendSyncResult(true, false, 0, pendingCount, message);
            }

            if (pendingCount == 0)
                return new BackendSyncResult(true, true, 0, 0, "Conectado; no hay eventos pendientes.");

            var settingsClientId = settings.ClientId;
            var sentCount = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<SyncQueueItem> batch;
                await using (var db = App.CreateDbContext())
                {
                    batch = await db.SyncQueue.Where(item => item.Status == "Pendiente")
                        .OrderBy(item => item.Id).Take(100).ToListAsync(cancellationToken);
                }

                if (batch.Count == 0)
                    break;

                var body = new SyncBatchRequest(settingsClientId, batch.Select(item => new SyncEventRequest(
                    item.Id, item.CreatedAtUtc, item.EntityType, item.EntityId, item.Operation, item.Payload)).ToList());
                using var response = await requestClient.PostAsJsonAsync("api/sync/events", body, JsonOptions, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"La API respondió {(int)response.StatusCode}: {TrimResponse(responseText)}");
                }

                var acknowledgment = await response.Content.ReadFromJsonAsync<SyncBatchResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidDataException("La API respondió sin confirmación de sincronización.");
                if (acknowledgment.Accepted + acknowledgment.Duplicates != batch.Count)
                    throw new InvalidDataException("La API no confirmó todos los eventos del lote.");

                await using var updateDb = App.CreateDbContext();
                var ids = batch.Select(item => item.Id).ToArray();
                var storedItems = await updateDb.SyncQueue.Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
                foreach (var item in storedItems)
                    item.Status = "Enviado";
                await updateDb.SaveChangesAsync(cancellationToken);
                sentCount += storedItems.Count;
            }

            var remaining = await PosService.GetPendingSyncCountAsync();
            return new BackendSyncResult(true, true, sentCount, remaining,
                sentCount == 0 ? "Conectado; no hay eventos pendientes." : $"Conectado; se enviaron {sentCount:N0} evento(s).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await IncrementPendingAttemptsAsync(cancellationToken);
            var pending = await PosService.GetPendingSyncCountAsync();
            return new BackendSyncResult(true, false, 0, pending, $"Sin conexión con la central: {TrimResponse(exception.Message)}");
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private static async Task IncrementPendingAttemptsAsync(CancellationToken cancellationToken)
    {
        await using var db = App.CreateDbContext();
        var batch = await db.SyncQueue.Where(item => item.Status == "Pendiente")
            .OrderBy(item => item.Id).Take(100).ToListAsync(cancellationToken);
        foreach (var item in batch)
            item.Attempts++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string TrimResponse(string value) =>
        value.Length <= 240 ? value : value[..240];

    private sealed record CentralProductResponse(
        int LocalProductId, string Barcode, string Name, string Category, string Unit,
        decimal UnitPrice, decimal TaxRate, decimal Stock, decimal MinimumStock,
        bool IsActive, DateTime UpdatedAtUtc);

    private sealed record CentralSaleResponse(
        long LocalSaleId, DateTime CreatedAtUtc, string Status, int CashierId, string CashierName,
        decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total, string Payload);

    private sealed class CentralSalePayload
    {
        public int? CashShiftId { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerDocument { get; set; }
        public List<CentralSaleItemPayload> Items { get; set; } = [];
        public List<CentralPaymentPayload> Payments { get; set; } = [];
    }

    private sealed class CentralSaleItemPayload
    {
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

    private sealed class CentralPaymentPayload
    {
        public string Method { get; set; } = "Efectivo";
        public decimal Amount { get; set; }
        public decimal Tendered { get; set; }
        public decimal Change { get; set; }
    }

    private sealed record SyncBatchRequest(string ClientId, List<SyncEventRequest> Events);
    private sealed record SyncEventRequest(long LocalEventId, DateTime CreatedAtUtc, string EntityType, string EntityId, string Operation, string Payload);
    private sealed record SyncBatchResponse(int Accepted, int Duplicates);
}
