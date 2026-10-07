using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Abasto.Backend.Data;
using Abasto.Backend.Domain;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var databaseProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var configuredConnection = builder.Configuration.GetConnectionString("CentralDatabase");
var databaseDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "SupermercadoPOS", "Backend");
var databasePath = Path.Combine(databaseDirectory, "supermercado-central.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
var apiKeyPath = Path.Combine(databaseDirectory, "api-key.txt");
var apiKey = builder.Configuration["Backend:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    apiKey = File.Exists(apiKeyPath)
        ? (await File.ReadAllTextAsync(apiKeyPath)).Trim()
        : "";
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        apiKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(apiKeyPath, apiKey);
    }
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Backend:ApiKey"] = apiKey
    });
    Console.WriteLine($"Clave de API central: {apiKey}");
    Console.WriteLine($"Clave guardada en: {apiKeyPath}");
}
var connectionString = string.IsNullOrWhiteSpace(configuredConnection)
    ? $"Data Source={databasePath}"
    : configuredConnection;

builder.Services.AddDbContext<CentralDbContext>(options =>
{
    if (databaseProvider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase)
        || databaseProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(configuredConnection))
            throw new InvalidOperationException("Configura ConnectionStrings:CentralDatabase para usar PostgreSQL.");
        options.UseNpgsql(connectionString);
    }
    else if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        options.UseSqlite(connectionString);
    else
        throw new InvalidOperationException("Database:Provider debe ser Sqlite o PostgreSql.");
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNameCaseInsensitive = true);

var app = builder.Build();
var demoBarcodes = new[]
{
    "7701001000011", "7701001000012", "7701001000013", "7701001000014",
    "7701001000015", "7701001000016", "7701001000017", "7701001000018"
};

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
    await db.Database.EnsureCreatedAsync();
    var demoProducts = await db.Products.Where(product => demoBarcodes.Contains(product.Barcode)).ToListAsync();
    foreach (var product in demoProducts)
    {
        var localProductId = product.LocalProductId.ToString();
        var demoEvents = await db.SyncEvents.Where(item => item.ClientId == product.ClientId
            && item.EntityType == "Product" && item.EntityId == localProductId).ToListAsync();
        db.SyncEvents.RemoveRange(demoEvents);
    }
    db.Products.RemoveRange(demoProducts);
    if (demoProducts.Count > 0)
        await db.SaveChangesAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "Abasto.Backend", utc = DateTime.UtcNow }));

app.MapGet("/api/sync/summary", async (HttpRequest request, IConfiguration configuration, CentralDbContext db) =>
{
    if (!IsAuthorized(request, configuration))
        return Results.Unauthorized();

    var count = await db.SyncEvents.LongCountAsync();
    var clients = await db.SyncEvents.Select(item => item.ClientId).Distinct().CountAsync();
    var latest = await db.SyncEvents.OrderByDescending(item => item.ReceivedAtUtc)
        .Select(item => (DateTime?)item.ReceivedAtUtc).FirstOrDefaultAsync();
    return Results.Ok(new { eventCount = count, clientCount = clients, latestEventAtUtc = latest });
});

app.MapPost("/api/sync/events", async (HttpRequest request, IConfiguration configuration, CentralDbContext db, SyncBatchRequest batch) =>
{
    if (!IsAuthorized(request, configuration))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(batch.ClientId) || batch.ClientId.Length > 80)
        return Results.BadRequest(new { error = "ClientId es obligatorio y debe tener máximo 80 caracteres." });
    if (batch.Events is null || batch.Events.Count is < 1 or > 100)
        return Results.BadRequest(new { error = "Envía entre 1 y 100 eventos por solicitud." });
    if (batch.Events.Any(item => item.LocalEventId <= 0
        || string.IsNullOrWhiteSpace(item.EntityType) || item.EntityType.Length > 80
        || string.IsNullOrWhiteSpace(item.EntityId) || item.EntityId.Length > 120
        || string.IsNullOrWhiteSpace(item.Operation) || item.Operation.Length > 80
        || item.Payload is null || item.Payload.Length > 256_000))
        return Results.BadRequest(new { error = "Uno o más eventos tienen campos inválidos o exceden el tamaño permitido." });

    var incomingIds = batch.Events.Select(item => item.LocalEventId).Distinct().ToArray();
    if (incomingIds.Length != batch.Events.Count)
        return Results.BadRequest(new { error = "El lote contiene identificadores de evento repetidos." });

    await using var transaction = await db.Database.BeginTransactionAsync();
    var existingIds = await db.SyncEvents
        .Where(item => item.ClientId == batch.ClientId && incomingIds.Contains(item.LocalEventId))
        .Select(item => item.LocalEventId)
        .ToListAsync();
    var existing = existingIds.ToHashSet();
    var inserted = 0;

    foreach (var item in batch.Events.OrderBy(item => item.LocalEventId))
    {
        if (existing.Contains(item.LocalEventId))
            continue;

        var stored = new CentralSyncEvent
        {
            ClientId = batch.ClientId,
            LocalEventId = item.LocalEventId,
            CreatedAtUtc = item.CreatedAtUtc.Kind == DateTimeKind.Local
                ? item.CreatedAtUtc.ToUniversalTime()
                : DateTime.SpecifyKind(item.CreatedAtUtc, DateTimeKind.Utc),
            ReceivedAtUtc = DateTime.UtcNow,
            EntityType = item.EntityType.Trim(),
            EntityId = item.EntityId.Trim(),
            Operation = item.Operation.Trim(),
            Payload = item.Payload
        };
        db.SyncEvents.Add(stored);
        await ProjectEventAsync(db, stored);
        inserted++;
    }

    await db.SaveChangesAsync();
    await transaction.CommitAsync();
    return Results.Ok(new { accepted = inserted, duplicates = batch.Events.Count - inserted });
});

app.MapGet("/api/sync/events", async (HttpRequest request, IConfiguration configuration, CentralDbContext db, string? clientId, int? take) =>
{
    if (!IsAuthorized(request, configuration))
        return Results.Unauthorized();
    var limit = Math.Clamp(take ?? 100, 1, 500);
    var query = db.SyncEvents.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(clientId))
        query = query.Where(item => item.ClientId == clientId);
    var events = await query.OrderByDescending(item => item.ReceivedAtUtc).Take(limit)
        .Select(item => new
        {
            item.Id,
            item.ClientId,
            item.LocalEventId,
            item.CreatedAtUtc,
            item.ReceivedAtUtc,
            item.EntityType,
            item.EntityId,
            item.Operation,
            item.Payload
        }).ToListAsync();
    return Results.Ok(events);
});

app.MapGet("/api/products", async (HttpRequest request, IConfiguration configuration, CentralDbContext db, string? clientId) =>
{
    if (!IsAuthorized(request, configuration))
        return Results.Unauthorized();
    var query = db.Products.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(clientId))
        query = query.Where(item => item.ClientId == clientId);
    return Results.Ok(await query.OrderBy(item => item.Name).ToListAsync());
});

app.MapGet("/api/sales", async (HttpRequest request, IConfiguration configuration, CentralDbContext db,
    string? clientId, DateTime? fromUtc, DateTime? toUtc, int? take) =>
{
    if (!IsAuthorized(request, configuration))
        return Results.Unauthorized();
    var query = db.Sales.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(clientId))
        query = query.Where(item => item.ClientId == clientId);
    if (fromUtc.HasValue)
        query = query.Where(item => item.CreatedAtUtc >= fromUtc.Value);
    if (toUtc.HasValue)
        query = query.Where(item => item.CreatedAtUtc < toUtc.Value);
    return Results.Ok(await query.OrderByDescending(item => item.CreatedAtUtc)
        .Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync());
});

app.Run();

static bool IsAuthorized(HttpRequest request, IConfiguration configuration)
{
    var expected = configuration["Backend:ApiKey"];
    if (string.IsNullOrWhiteSpace(expected)
        || !request.Headers.TryGetValue("X-Api-Key", out var supplied)
        || string.IsNullOrEmpty(supplied))
        return false;

    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied.ToString());
    return expectedBytes.Length == suppliedBytes.Length
        && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
}

static async Task ProjectEventAsync(CentralDbContext db, CentralSyncEvent syncEvent)
{
    if (syncEvent.EntityType.Equals("Product", StringComparison.OrdinalIgnoreCase))
    {
        using var payload = JsonDocument.Parse(syncEvent.Payload);
        var root = payload.RootElement;
        var barcode = ReadString(root, "barcode");
        if (string.IsNullOrWhiteSpace(barcode))
            return;
        var localProductId = ReadInt(root, "id");
        var product = localProductId is > 0
            ? db.Products.Local.FirstOrDefault(item => item.ClientId == syncEvent.ClientId
                && item.LocalProductId == localProductId.Value)
                ?? await db.Products.FirstOrDefaultAsync(item => item.ClientId == syncEvent.ClientId
                    && item.LocalProductId == localProductId.Value)
            : null;
        product ??= db.Products.Local.FirstOrDefault(item =>
            item.ClientId == syncEvent.ClientId && item.Barcode == barcode)
            ?? await db.Products.FirstOrDefaultAsync(item =>
                item.ClientId == syncEvent.ClientId && item.Barcode == barcode);
        if (product is null)
        {
            var name = ReadString(root, "name");
            if (string.IsNullOrWhiteSpace(name))
                return;
            product = new CentralProduct { ClientId = syncEvent.ClientId, Barcode = barcode };
            db.Products.Add(product);
        }

        product.LocalProductId = ReadInt(root, "id") ?? product.LocalProductId;
        product.Name = ReadString(root, "name") ?? product.Name;
        product.Category = ReadString(root, "category") ?? product.Category;
        product.Unit = ReadString(root, "unit") ?? product.Unit;
        product.UnitPrice = ReadDecimal(root, "unitPrice") ?? product.UnitPrice;
        product.TaxRate = ReadDecimal(root, "taxRate") ?? product.TaxRate;
        product.Stock = ReadDecimal(root, "stock") ?? product.Stock;
        product.MinimumStock = ReadDecimal(root, "minimumStock") ?? product.MinimumStock;
        product.IsActive = ReadBool(root, "isActive") ?? product.IsActive;
        product.UpdatedAtUtc = ReadDate(root, "updatedAtUtc") ?? syncEvent.CreatedAtUtc;
        return;
    }

    if (!syncEvent.EntityType.Equals("Sale", StringComparison.OrdinalIgnoreCase))
        return;

    using var salePayload = JsonDocument.Parse(syncEvent.Payload);
    var saleRoot = salePayload.RootElement;
    var saleId = ReadLong(saleRoot, "id");
    if (saleId is null)
        return;
    var sale = db.Sales.Local.FirstOrDefault(item =>
        item.ClientId == syncEvent.ClientId && item.LocalSaleId == saleId.Value)
        ?? await db.Sales.FirstOrDefaultAsync(item =>
            item.ClientId == syncEvent.ClientId && item.LocalSaleId == saleId.Value);
    if (sale is null)
    {
        sale = new CentralSale { ClientId = syncEvent.ClientId, LocalSaleId = saleId.Value };
        db.Sales.Add(sale);
    }

    if (syncEvent.Operation.Equals("SaleVoided", StringComparison.OrdinalIgnoreCase))
        sale.Status = "Anulada";
    else if (syncEvent.Operation.Equals("SaleCompleted", StringComparison.OrdinalIgnoreCase))
        sale.Status = "Completada";
    sale.CreatedAtUtc = ReadDate(saleRoot, "createdAtUtc") ?? sale.CreatedAtUtc;
    sale.CashierId = ReadInt(saleRoot, "cashierId") ?? sale.CashierId;
    sale.CashierName = ReadString(saleRoot, "cashierName") ?? sale.CashierName;
    sale.Subtotal = ReadDecimal(saleRoot, "subtotal") ?? sale.Subtotal;
    sale.DiscountTotal = ReadDecimal(saleRoot, "discountTotal") ?? sale.DiscountTotal;
    sale.TaxTotal = ReadDecimal(saleRoot, "taxTotal") ?? sale.TaxTotal;
    sale.Total = ReadDecimal(saleRoot, "total") ?? sale.Total;
    if (syncEvent.Operation.Equals("SaleCompleted", StringComparison.OrdinalIgnoreCase)
        || syncEvent.Operation.Equals("SaleVoided", StringComparison.OrdinalIgnoreCase))
        sale.Payload = syncEvent.Payload;
}

static string? ReadString(JsonElement element, string name) =>
    TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

static int? ReadInt(JsonElement element, string name) =>
    TryProperty(element, name, out var value) && value.TryGetInt32(out var result) ? result : null;

static long? ReadLong(JsonElement element, string name) =>
    TryProperty(element, name, out var value) && value.TryGetInt64(out var result) ? result : null;

static decimal? ReadDecimal(JsonElement element, string name) =>
    TryProperty(element, name, out var value) && value.TryGetDecimal(out var result) ? result : null;

static bool? ReadBool(JsonElement element, string name) =>
    TryProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

static DateTime? ReadDate(JsonElement element, string name) =>
    TryProperty(element, name, out var value) && value.TryGetDateTime(out var result) ? result.ToUniversalTime() : null;

static bool TryProperty(JsonElement element, string name, out JsonElement value)
{
    foreach (var property in element.EnumerateObject())
    {
        if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            value = property.Value;
            return true;
        }
    }
    value = default;
    return false;
}
