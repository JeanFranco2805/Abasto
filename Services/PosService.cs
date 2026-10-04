using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using SupermercadoPOS.Data;
using SupermercadoPOS.Domain;

namespace SupermercadoPOS.Services;

public static class PosService
{
    private const int PinIterations = 120_000;
    private static readonly SemaphoreSlim BackupLock = new(1, 1);

    public static async Task InitializeAsync(string databasePath)
    {
        await using var db = PosDbContext.Create(databasePath);
        await db.Database.EnsureCreatedAsync();

        if (!await db.Products.AnyAsync())
        {
            db.Products.AddRange(
                new Product { Barcode = "7701001000011", Name = "Arroz blanco 1 kg", Category = "Granos", Unit = "UND", UnitPrice = 5200m, TaxRate = 0m, Stock = 48m, MinimumStock = 8m },
                new Product { Barcode = "7701001000012", Name = "Leche entera 1 L", Category = "Lácteos", Unit = "UND", UnitPrice = 4300m, TaxRate = 0m, Stock = 32m, MinimumStock = 6m },
                new Product { Barcode = "7701001000013", Name = "Huevos AA x 12", Category = "Refrigerados", Unit = "UND", UnitPrice = 10800m, TaxRate = 0m, Stock = 24m, MinimumStock = 5m },
                new Product { Barcode = "7701001000014", Name = "Pan tajado", Category = "Panadería", Unit = "UND", UnitPrice = 4700m, TaxRate = 0.05m, Stock = 18m, MinimumStock = 4m },
                new Product { Barcode = "7701001000015", Name = "Manzana roja", Category = "Frutas", Unit = "KG", UnitPrice = 8900m, TaxRate = 0m, Stock = 15m, MinimumStock = 3m },
                new Product { Barcode = "7701001000016", Name = "Café molido 250 g", Category = "Despensa", Unit = "UND", UnitPrice = 14900m, TaxRate = 0.05m, Stock = 20m, MinimumStock = 4m },
                new Product { Barcode = "7701001000017", Name = "Jabón para platos", Category = "Aseo", Unit = "UND", UnitPrice = 6200m, TaxRate = 0.19m, Stock = 14m, MinimumStock = 3m },
                new Product { Barcode = "7701001000018", Name = "Tomate chonto", Category = "Verduras", Unit = "KG", UnitPrice = 3900m, TaxRate = 0m, Stock = 12m, MinimumStock = 2m }
            );
            await db.SaveChangesAsync();
        }

        if (!await db.Users.AnyAsync())
        {
            db.Users.Add(CreateUser("cajero", "Cajero de demostración", "1111", "Cajero"));
            db.Users.Add(CreateUser("supervisor", "Supervisor de demostración", "1234", "Supervisor"));
            db.Users.Add(CreateUser("admin", "Administrador de demostración", "2468", "Administrador"));
            await db.SaveChangesAsync();
        }
    }

    public static async Task CreateDailyBackupAsync(string databasePath)
    {
        await BackupLock.WaitAsync();
        string? temporaryPath = null;
        try
        {
            var backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "backups");
            Directory.CreateDirectory(backupDirectory);
            var dateStamp = DateTime.Now.ToString("yyyyMMdd");
            var backupPath = Path.Combine(backupDirectory, $"pos-{dateStamp}.db");
            temporaryPath = backupPath + $".{Guid.NewGuid():N}.tmp";

            var sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString();
            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            await using (var source = new SqliteConnection(sourceConnectionString))
            await using (var destination = new SqliteConnection(destinationConnectionString))
            {
                await source.OpenAsync();
                await destination.OpenAsync();
                source.BackupDatabase(destination);
            }

            // Pooling is disabled above so the destination file handle is closed before the rename.
            File.Move(temporaryPath, backupPath, true);
            temporaryPath = null;

            foreach (var oldBackup in new DirectoryInfo(backupDirectory).GetFiles("pos-*.db")
                         .OrderByDescending(f => f.Name).Skip(14))
                oldBackup.Delete();
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            BackupLock.Release();
        }
    }

    public static async Task<PosUser?> AuthenticateAsync(string username, string pin)
    {
        await using var db = App.CreateDbContext();
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username.Trim().ToLower() && u.IsActive);
        if (user is null)
            return null;

        var salt = Convert.FromBase64String(user.PinSalt);
        var candidate = Rfc2898DeriveBytes.Pbkdf2(pin, salt, PinIterations, HashAlgorithmName.SHA256, 32);
        var expected = Convert.FromBase64String(user.PinHash);
        return CryptographicOperations.FixedTimeEquals(candidate, expected) ? user : null;
    }

    public static async Task<List<PosUserSummary>> GetUsersAsync()
    {
        RequireAdministrator();
        await using var db = App.CreateDbContext();
        return await db.Users.AsNoTracking()
            .OrderBy(u => u.DisplayName)
            .Select(u => new PosUserSummary(u.Id, u.Username, u.DisplayName, u.Role, u.IsActive))
            .ToListAsync();
    }

    public static async Task CreateUserAsync(string username, string displayName, string role, string pin)
    {
        RequireAdministrator();
        username = username.Trim().ToLowerInvariant();
        displayName = displayName.Trim();
        ValidateUserInput(username, displayName, role, pin);
        await using var db = App.CreateDbContext();
        if (await db.Users.AnyAsync(u => u.Username == username))
            throw new InvalidOperationException("Ya existe un usuario con ese nombre de acceso.");
        db.Users.Add(CreateUser(username, displayName, pin, role));
        await db.SaveChangesAsync();
        await AddAuditAsync(db, "Usuario creado", $"{displayName} ({username}) con perfil {role}");
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task ResetUserPinAsync(int userId, string pin)
    {
        RequireAdministrator();
        ValidatePin(pin);
        await using var db = App.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new InvalidOperationException("No se encontró el usuario.");
        SetPin(user, pin);
        await db.SaveChangesAsync();
        await AddAuditAsync(db, "PIN restablecido", $"Usuario {user.DisplayName} ({user.Username})");
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task SetUserActiveAsync(int userId, bool isActive)
    {
        RequireAdministrator();
        var currentUser = RequireUser();
        if (!isActive && currentUser.Id == userId)
            throw new InvalidOperationException("No puedes desactivar el usuario de la sesión actual.");

        await using var db = App.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new InvalidOperationException("No se encontró el usuario.");
        if (!isActive && user.Role == "Administrador"
            && await db.Users.CountAsync(u => u.IsActive && u.Role == "Administrador") <= 1)
            throw new InvalidOperationException("Debe quedar al menos un administrador activo.");

        user.IsActive = isActive;
        await db.SaveChangesAsync();
        await AddAuditAsync(db, isActive ? "Usuario activado" : "Usuario desactivado", $"{user.DisplayName} ({user.Username})");
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task<List<Product>> GetProductsAsync(string? query = null)
    {
        await using var db = App.CreateDbContext();
        var products = db.Products.AsNoTracking().Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            products = products.Where(p => p.Name.Contains(term) || p.Barcode.Contains(term) || p.Category.Contains(term));
        }
        return await products.OrderBy(p => p.Name).ToListAsync();
    }

    public static async Task<Product?> FindProductByBarcodeAsync(string barcode)
    {
        await using var db = App.CreateDbContext();
        return await db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Barcode == barcode.Trim() && p.IsActive);
    }

    public static async Task SaveProductAsync(Product product)
    {
        var user = RequireUser();
        if (user.Role == "Cajero")
            throw new InvalidOperationException("Se requiere perfil de supervisor para administrar productos.");
        await using var db = App.CreateDbContext();
        if (string.IsNullOrWhiteSpace(product.Barcode) || string.IsNullOrWhiteSpace(product.Name))
            throw new InvalidOperationException("El código de barras y el nombre son obligatorios.");
        if (product.UnitPrice < 0 || product.Stock < 0 || product.TaxRate < 0 || product.TaxRate > 1)
            throw new InvalidOperationException("Revisa el precio, el inventario y el porcentaje de impuesto.");

        var duplicate = await db.Products.AnyAsync(p => p.Barcode == product.Barcode && p.Id != product.Id);
        if (duplicate)
            throw new InvalidOperationException("Ya existe un producto con ese código de barras.");

        product.UpdatedAtUtc = DateTime.UtcNow;
        if (product.Id == 0)
        {
            db.Products.Add(product);
            await db.SaveChangesAsync();
            await AddAuditAsync(db, "Producto creado", $"{product.Name} ({product.Barcode})");
        }
        else
        {
            var stored = await db.Products.FirstOrDefaultAsync(p => p.Id == product.Id)
                ?? throw new InvalidOperationException("No se encontró el producto.");
            stored.Barcode = product.Barcode.Trim();
            stored.Name = product.Name.Trim();
            stored.Category = product.Category.Trim();
            stored.Unit = product.Unit;
            stored.UnitPrice = product.UnitPrice;
            stored.TaxRate = product.TaxRate;
            stored.Stock = product.Stock;
            stored.MinimumStock = product.MinimumStock;
            stored.IsActive = product.IsActive;
            stored.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await AddAuditAsync(db, "Producto actualizado", $"{stored.Name} ({stored.Barcode})");
        }
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task<CashShift?> GetOpenShiftAsync(int cashierId)
    {
        await using var db = App.CreateDbContext();
        return await db.CashShifts.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CashierId == cashierId && s.Status == "Abierto");
    }

    public static async Task OpenShiftAsync(decimal openingFloat)
    {
        var user = RequireUser();
        if (openingFloat < 0)
            throw new InvalidOperationException("El fondo inicial no puede ser negativo.");

        await using var db = App.CreateDbContext();
        if (await db.CashShifts.AnyAsync(s => s.CashierId == user.Id && s.Status == "Abierto"))
            throw new InvalidOperationException("Ya tienes un turno abierto.");

        var shift = new CashShift
        {
            CashierId = user.Id,
            CashierName = user.DisplayName,
            OpeningFloat = openingFloat,
            OpenedAtUtc = DateTime.UtcNow
        };
        db.CashShifts.Add(shift);
        await db.SaveChangesAsync();
        await AddAuditAsync(db, "Turno abierto", $"Fondo inicial: {openingFloat:C0}");
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task<decimal> CalculateExpectedCashAsync(int shiftId)
    {
        await using var db = App.CreateDbContext();
        return await CalculateExpectedCashAsync(db, shiftId);
    }

    private static async Task<decimal> CalculateExpectedCashAsync(PosDbContext db, int shiftId)
    {
        var shift = await db.CashShifts.FirstOrDefaultAsync(s => s.Id == shiftId)
            ?? throw new InvalidOperationException("No se encontró el turno.");
        var cashPayments = await db.Sales
            .Where(s => s.CashShiftId == shiftId && (s.Status == "Completada" || s.Status == "Anulada"))
            .SelectMany(s => s.Payments)
            .Where(p => p.Method == "Efectivo")
            .Select(p => p.Amount)
            .ToListAsync();
        var movements = await db.CashMovements.AsNoTracking()
            .Where(m => m.CashShiftId == shiftId)
            .ToListAsync();
        var cashSales = cashPayments.Sum();
        var cashIn = movements.Where(m => m.IsCashIn).Sum(m => m.Amount);
        var cashOut = movements.Where(m => !m.IsCashIn).Sum(m => m.Amount);
        return Money(shift.OpeningFloat + cashSales + cashIn - cashOut);
    }

    public static async Task RegisterCashMovementAsync(bool isCashIn, decimal amount, string reason)
    {
        var user = RequireUser();
        if (amount <= 0 || string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Ingresa un valor mayor que cero y el motivo del movimiento.");

        await using var db = App.CreateDbContext();
        var shift = await db.CashShifts.FirstOrDefaultAsync(s => s.CashierId == user.Id && s.Status == "Abierto")
            ?? throw new InvalidOperationException("No tienes un turno abierto.");
        db.CashMovements.Add(new CashMovement
        {
            CashShiftId = shift.Id,
            UserId = user.Id,
            UserName = user.DisplayName,
            IsCashIn = isCashIn,
            Amount = Money(amount),
            Reason = reason.Trim()
        });
        await db.SaveChangesAsync();
        await AddAuditAsync(db, isCashIn ? "Entrada de efectivo" : "Salida de efectivo", $"{amount:C0} - {reason.Trim()}");
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task CloseShiftAsync(int shiftId, decimal countedCash)
    {
        var user = RequireUser();
        await using var db = App.CreateDbContext();
        var shift = await db.CashShifts.FirstOrDefaultAsync(s => s.Id == shiftId && s.CashierId == user.Id && s.Status == "Abierto")
            ?? throw new InvalidOperationException("No se encontró un turno abierto para cerrar.");
        shift.ExpectedCash = await CalculateExpectedCashAsync(db, shift.Id);
        shift.CountedCash = Money(countedCash);
        shift.Difference = Money(shift.CountedCash - shift.ExpectedCash);
        shift.Status = "Cerrado";
        shift.ClosedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await AddAuditAsync(db, "Turno cerrado",
            $"Esperado: {shift.ExpectedCash:C0}; contado: {shift.CountedCash:C0}; diferencia: {shift.Difference:C0}");
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task<Sale> SuspendSaleAsync(IReadOnlyCollection<SaleLineDraft> lines, long? suspendedSaleId = null)
    {
        var user = RequireUser();
        if (lines.Count == 0)
            throw new InvalidOperationException("Agrega productos antes de suspender la venta.");

        await using var db = App.CreateDbContext();
        var shift = await db.CashShifts.FirstOrDefaultAsync(s => s.CashierId == user.Id && s.Status == "Abierto")
            ?? throw new InvalidOperationException("Abre un turno antes de suspender una venta.");
        Sale sale;
        if (suspendedSaleId.HasValue)
        {
            sale = await db.Sales.Include(s => s.Items)
                .FirstOrDefaultAsync(s => s.Id == suspendedSaleId && s.Status == "Suspendida")
                ?? throw new InvalidOperationException("La venta suspendida ya no está disponible.");
            if (sale.CashierId != user.Id && user.Role == "Cajero")
                throw new InvalidOperationException("No tienes permiso para modificar esta venta suspendida.");
            db.SaleItems.RemoveRange(sale.Items);
            sale.Items.Clear();
        }
        else
        {
            sale = new Sale();
            db.Sales.Add(sale);
        }

        sale.CreatedAtUtc = DateTime.UtcNow;
        sale.Status = "Suspendida";
        sale.CashierId = user.Id;
        sale.CashierName = user.DisplayName;
        sale.CashShiftId = shift.Id;
        sale.Subtotal = 0;
        sale.DiscountTotal = 0;
        sale.TaxTotal = 0;
        sale.Total = 0;

        foreach (var draft in lines)
        {
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == draft.ProductId && p.IsActive)
                ?? throw new InvalidOperationException($"El producto {draft.ProductName} ya no está disponible.");
            if (draft.Quantity <= 0 || draft.UnitPrice < 0 || draft.TaxRate is < 0 or > 1
                || draft.DiscountRate is < 0 or > 1
                || (draft.DiscountRate > 0 && string.IsNullOrWhiteSpace(draft.DiscountApprovedBy)))
                throw new InvalidOperationException("La venta contiene datos de producto no válidos.");
            var subtotal = Money(draft.UnitPrice * draft.Quantity);
            var discount = Money(subtotal * draft.DiscountRate);
            var tax = Money((subtotal - discount) * draft.TaxRate);
            var total = Money(subtotal - discount + tax);
            sale.Items.Add(new SaleItem
            {
                ProductId = product.Id,
                ProductName = draft.ProductName,
                Barcode = draft.Barcode,
                Unit = draft.Unit,
                Quantity = draft.Quantity,
                UnitPrice = draft.UnitPrice,
                TaxRate = draft.TaxRate,
                DiscountApprovedBy = draft.DiscountApprovedBy,
                DiscountAmount = discount,
                LineSubtotal = subtotal,
                LineTax = tax,
                LineTotal = total
            });
            sale.Subtotal += subtotal;
            sale.DiscountTotal += discount;
            sale.TaxTotal += tax;
        }
        sale.Subtotal = Money(sale.Subtotal);
        sale.DiscountTotal = Money(sale.DiscountTotal);
        sale.TaxTotal = Money(sale.TaxTotal);
        sale.Total = Money(sale.Subtotal - sale.DiscountTotal + sale.TaxTotal);
        await db.SaveChangesAsync();
        await AddAuditAsync(db, "Venta suspendida", $"Venta #{sale.Id} por {sale.Total:C0}");
        await CreateDailyBackupAsync(App.DatabasePath);
        return sale;
    }

    public static async Task<List<Sale>> GetSuspendedSalesAsync()
    {
        var user = RequireUser();
        await using var db = App.CreateDbContext();
        var sales = db.Sales.AsNoTracking().Include(s => s.Items).Where(s => s.Status == "Suspendida");
        if (user.Role == "Cajero")
            return await sales.Where(s => s.CashierId == user.Id).OrderByDescending(s => s.CreatedAtUtc).ToListAsync();
        return await sales.OrderByDescending(s => s.CreatedAtUtc).ToListAsync();
    }

    public static async Task<long> CompleteSaleAsync(
        IReadOnlyCollection<SaleLineDraft> lines,
        IReadOnlyCollection<PaymentDraft> payments,
        long? suspendedSaleId = null,
        string? customerName = null,
        string? customerDocument = null)
    {
        var user = RequireUser();
        if (lines.Count == 0)
            throw new InvalidOperationException("La venta no tiene productos.");

        await using var db = App.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var shift = await db.CashShifts.FirstOrDefaultAsync(s => s.CashierId == user.Id && s.Status == "Abierto")
            ?? throw new InvalidOperationException("Abre un turno antes de cobrar.");

        Sale sale;
        if (suspendedSaleId.HasValue)
        {
            sale = await db.Sales.Include(s => s.Items).Include(s => s.Payments)
                .FirstOrDefaultAsync(s => s.Id == suspendedSaleId && s.Status == "Suspendida")
                ?? throw new InvalidOperationException("La venta suspendida ya no está disponible.");
            if (sale.CashierId != user.Id && user.Role == "Cajero")
                throw new InvalidOperationException("No tienes permiso para cobrar esta venta suspendida.");
            db.SaleItems.RemoveRange(sale.Items);
            db.Payments.RemoveRange(sale.Payments);
            sale.Items.Clear();
            sale.Payments.Clear();
        }
        else
            sale = new Sale();

        sale.CreatedAtUtc = DateTime.UtcNow;
        sale.Status = "Completada";
        sale.CashierId = user.Id;
        sale.CashierName = user.DisplayName;
        sale.CashShiftId = shift.Id;
        sale.CustomerName = string.IsNullOrWhiteSpace(customerName) ? null : customerName.Trim();
        sale.CustomerDocument = string.IsNullOrWhiteSpace(customerDocument) ? null : customerDocument.Trim();
        sale.Subtotal = 0;
        sale.DiscountTotal = 0;
        sale.TaxTotal = 0;
        sale.Total = 0;

        foreach (var draft in lines)
        {
            if (draft.Quantity <= 0)
                throw new InvalidOperationException("La cantidad de cada producto debe ser mayor que cero.");
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == draft.ProductId && p.IsActive)
                ?? throw new InvalidOperationException("Uno de los productos ya no está disponible.");
            if (product.Stock < draft.Quantity)
                throw new InvalidOperationException($"Existencias insuficientes para {product.Name}. Disponibles: {product.Stock:N3} {product.Unit}.");
            if (draft.UnitPrice < 0 || draft.TaxRate is < 0 or > 1 || draft.DiscountRate is < 0 or > 1
                || (draft.DiscountRate > 0 && string.IsNullOrWhiteSpace(draft.DiscountApprovedBy)))
                throw new InvalidOperationException($"El precio o el impuesto de {product.Name} no es válido.");

            var subtotal = Money(draft.UnitPrice * draft.Quantity);
            var discount = Money(subtotal * draft.DiscountRate);
            var tax = Money((subtotal - discount) * draft.TaxRate);
            var total = Money(subtotal - discount + tax);
            sale.Items.Add(new SaleItem
            {
                ProductId = product.Id,
                ProductName = draft.ProductName,
                Barcode = draft.Barcode,
                Unit = draft.Unit,
                Quantity = draft.Quantity,
                UnitPrice = draft.UnitPrice,
                TaxRate = draft.TaxRate,
                DiscountApprovedBy = draft.DiscountApprovedBy,
                DiscountAmount = discount,
                LineSubtotal = subtotal,
                LineTax = tax,
                LineTotal = total
            });
            product.Stock -= draft.Quantity;
            product.UpdatedAtUtc = DateTime.UtcNow;
            sale.Subtotal += subtotal;
            sale.DiscountTotal += discount;
            sale.TaxTotal += tax;
            db.SyncQueue.Add(new SyncQueueItem
            {
                EntityType = "Product",
                EntityId = product.Id.ToString(),
                Operation = "StockUpdated",
                Payload = JsonSerializer.Serialize(new { product.Id, product.Barcode, product.Stock, updatedAtUtc = product.UpdatedAtUtc })
            });
        }

        sale.Subtotal = Money(sale.Subtotal);
        sale.DiscountTotal = Money(sale.DiscountTotal);
        sale.TaxTotal = Money(sale.TaxTotal);
        sale.Total = Money(sale.Subtotal - sale.DiscountTotal + sale.TaxTotal);
        var paid = Money(payments.Sum(p => p.Amount));
        if (payments.Count == 0 || paid != sale.Total)
            throw new InvalidOperationException("Los pagos deben cubrir exactamente el total de la venta.");

        foreach (var payment in payments)
        {
            if (payment.Amount <= 0 || payment.Change < 0)
                throw new InvalidOperationException("Hay un pago con valores no válidos.");
            sale.Payments.Add(new Payment
            {
                Method = payment.Method,
                Amount = Money(payment.Amount),
                Tendered = Money(payment.Tendered),
                Change = Money(payment.Change)
            });
        }

        if (!suspendedSaleId.HasValue)
            db.Sales.Add(sale);
        await db.SaveChangesAsync();
        db.SyncQueue.Add(new SyncQueueItem
        {
            EntityType = "Sale",
            EntityId = sale.Id.ToString(),
            Operation = "SaleCompleted",
            Payload = JsonSerializer.Serialize(new
            {
                sale.Id,
                sale.CreatedAtUtc,
                sale.CashierId,
                sale.CashierName,
                sale.Subtotal,
                sale.DiscountTotal,
                sale.TaxTotal,
                sale.Total,
                Items = sale.Items.Select(i => new { i.ProductId, i.Barcode, i.ProductName, i.Quantity, i.UnitPrice, i.TaxRate, i.DiscountAmount, i.DiscountApprovedBy, i.LineTotal }),
                Payments = sale.Payments.Select(p => new { p.Method, p.Amount, p.Tendered, p.Change })
            })
        });
        await AddAuditAsync(db, "Venta completada", $"Venta #{sale.Id} por {sale.Total:C0}");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await CreateDailyBackupAsync(App.DatabasePath);
        return sale.Id;
    }

    public static async Task<Sale?> GetSaleAsync(long saleId)
    {
        await using var db = App.CreateDbContext();
        return await db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == saleId);
    }

    public static async Task VoidCompletedCashSaleAsync(long saleId, PosUser supervisor)
    {
        var cashier = RequireUser();
        if (supervisor.Role == "Cajero")
            throw new InvalidOperationException("Se requiere autorización de supervisor.");

        await using var db = App.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var verifiedSupervisor = await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == supervisor.Id && u.IsActive && u.Role != "Cajero");
        if (!verifiedSupervisor)
            throw new InvalidOperationException("La autorización de supervisor ya no es válida.");

        var sale = await db.Sales.Include(s => s.Items).Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == saleId && s.Status == "Completada")
            ?? throw new InvalidOperationException("La venta no existe o ya fue anulada.");
        var currentShift = await db.CashShifts
            .FirstOrDefaultAsync(s => s.CashierId == cashier.Id && s.Status == "Abierto")
            ?? throw new InvalidOperationException("Abre un turno antes de procesar la anulación.");
        if (sale.CashShiftId != currentShift.Id)
            throw new InvalidOperationException("Por ahora solo se pueden anular ventas del turno que sigue abierto.");
        if (sale.Payments.Any(p => p.Method != "Efectivo"))
            throw new InvalidOperationException("La devolución de tarjeta o transferencia requiere la integración del datáfono.");

        foreach (var item in sale.Items)
        {
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == item.ProductId)
                ?? throw new InvalidOperationException($"No se encontró el producto {item.ProductName} para reponer inventario.");
            product.Stock += item.Quantity;
            product.UpdatedAtUtc = DateTime.UtcNow;
        }

        var refund = Money(sale.Payments.Sum(p => p.Amount));
        sale.Status = "Anulada";
        db.CashMovements.Add(new CashMovement
        {
            CashShiftId = currentShift.Id,
            UserId = supervisor.Id,
            UserName = supervisor.DisplayName,
            IsCashIn = false,
            Amount = refund,
            Reason = $"Devolución por anulación de venta #{sale.Id}"
        });
        db.SyncQueue.Add(new SyncQueueItem
        {
            EntityType = "Sale",
            EntityId = sale.Id.ToString(),
            Operation = "SaleVoided",
            Payload = JsonSerializer.Serialize(new { sale.Id, sale.Status, refundedAmount = refund })
        });
        await AddAuditAsync(db, "Venta anulada",
            $"Venta #{sale.Id}; devolución en efectivo: {refund:C0}; autorizó {supervisor.DisplayName}");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task<SalesSummary> GetTodaySummaryAsync()
    {
        var localStart = DateTime.Today;
        var startUtc = localStart.ToUniversalTime();
        var endUtc = localStart.AddDays(1).ToUniversalTime();
        await using var db = App.CreateDbContext();
        var sales = await db.Sales.AsNoTracking()
            .Include(s => s.Payments)
            .Where(s => s.Status == "Completada" && s.CreatedAtUtc >= startUtc && s.CreatedAtUtc < endUtc)
            .ToListAsync();
        var payments = sales.SelectMany(s => s.Payments).ToList();
        return new SalesSummary(
            sales.Count,
            sales.Sum(s => s.Total),
            payments.Where(p => p.Method == "Efectivo").Sum(p => p.Amount),
            payments.Where(p => p.Method == "Tarjeta").Sum(p => p.Amount),
            payments.Where(p => p.Method is not ("Efectivo" or "Tarjeta")).Sum(p => p.Amount));
    }

    public static async Task<List<Sale>> GetRecentSalesAsync(int count = 15)
    {
        await using var db = App.CreateDbContext();
        return await db.Sales.AsNoTracking()
            .Include(s => s.Payments)
            .Where(s => s.Status == "Completada")
            .OrderByDescending(s => s.CreatedAtUtc)
            .Take(count)
            .ToListAsync();
    }

    public static async Task<List<Sale>> GetSalesBetweenAsync(DateTime fromLocal, DateTime toLocal)
    {
        var startUtc = fromLocal.Date.ToUniversalTime();
        var endUtc = toLocal.Date.AddDays(1).ToUniversalTime();
        await using var db = App.CreateDbContext();
        return await db.Sales.AsNoTracking()
            .Include(s => s.Payments)
            .Where(s => (s.Status == "Completada" || s.Status == "Anulada")
                && s.CreatedAtUtc >= startUtc && s.CreatedAtUtc < endUtc)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync();
    }

    public static async Task<SalesSummary> GetSalesSummaryBetweenAsync(DateTime fromLocal, DateTime toLocal)
    {
        var sales = await GetSalesBetweenAsync(fromLocal, toLocal);
        var completed = sales.Where(s => s.Status == "Completada").ToList();
        var payments = completed.SelectMany(s => s.Payments).ToList();
        return new SalesSummary(
            completed.Count,
            completed.Sum(s => s.Total),
            payments.Where(p => p.Method == "Efectivo").Sum(p => p.Amount),
            payments.Where(p => p.Method == "Tarjeta").Sum(p => p.Amount),
            payments.Where(p => p.Method is not ("Efectivo" or "Tarjeta")).Sum(p => p.Amount));
    }

    public static async Task<List<ProductPerformanceRow>> GetProductPerformanceAsync(DateTime fromLocal, DateTime toLocal)
    {
        var startUtc = fromLocal.Date.ToUniversalTime();
        var endUtc = toLocal.Date.AddDays(1).ToUniversalTime();
        await using var db = App.CreateDbContext();
        var items = await db.SaleItems.AsNoTracking()
            .Include(i => i.Sale)
            .Where(i => i.Sale != null && i.Sale.Status == "Completada"
                && i.Sale.CreatedAtUtc >= startUtc && i.Sale.CreatedAtUtc < endUtc)
            .ToListAsync();
        var totals = items.GroupBy(i => i.ProductId)
            .ToDictionary(group => group.Key, group => (Quantity: group.Sum(i => i.Quantity), Revenue: group.Sum(i => i.LineTotal)));
        var products = await db.Products.AsNoTracking().Where(product => product.IsActive).ToListAsync();
        return products.Select(product =>
            {
                var total = totals.GetValueOrDefault(product.Id);
                return new ProductPerformanceRow(product.Name, product.Barcode, total.Quantity, total.Revenue);
            })
            .OrderByDescending(row => row.QuantitySold)
            .ToList();
    }

    public static async Task<List<CashShift>> GetRecentCashShiftsAsync(int count = 30)
    {
        await using var db = App.CreateDbContext();
        return await db.CashShifts.AsNoTracking()
            .OrderByDescending(s => s.OpenedAtUtc)
            .Take(count)
            .ToListAsync();
    }

    public static async Task<List<CashMovement>> GetCashMovementsAsync(int shiftId)
    {
        await using var db = App.CreateDbContext();
        return await db.CashMovements.AsNoTracking()
            .Where(m => m.CashShiftId == shiftId)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToListAsync();
    }

    public static async Task<List<AuditEvent>> GetRecentAuditEventsAsync(int count = 250)
    {
        await using var db = App.CreateDbContext();
        return await db.AuditEvents.AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(count)
            .ToListAsync();
    }

    public static async Task<List<SyncQueueItem>> GetRecentSyncQueueAsync(int count = 250)
    {
        await using var db = App.CreateDbContext();
        return await db.SyncQueue.AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(count)
            .ToListAsync();
    }

    public static async Task<int> GetPendingSyncCountAsync()
    {
        await using var db = App.CreateDbContext();
        return await db.SyncQueue.CountAsync(item => item.Status == "Pendiente");
    }

    private static PosUser RequireUser() =>
        Session.CurrentUser ?? throw new InvalidOperationException("La sesión no está iniciada.");

    private static void RequireAdministrator()
    {
        if (RequireUser().Role != "Administrador")
            throw new InvalidOperationException("Se requiere el perfil de administrador para administrar usuarios.");
    }

    private static void ValidateUserInput(string username, string displayName, string role, string pin)
    {
        if (username.Length < 3 || username.Any(char.IsWhiteSpace) || displayName.Length < 2)
            throw new InvalidOperationException("Ingresa un usuario sin espacios (mínimo 3 caracteres) y un nombre válido.");
        if (role is not ("Cajero" or "Supervisor" or "Administrador"))
            throw new InvalidOperationException("Selecciona un perfil válido.");
        ValidatePin(pin);
    }

    private static void ValidatePin(string pin)
    {
        if (pin.Length is < 4 or > 12 || pin.Any(character => !char.IsDigit(character)))
            throw new InvalidOperationException("El PIN debe contener entre 4 y 12 dígitos.");
    }

    private static async Task AddAuditAsync(PosDbContext db, string operation, string details)
    {
        var user = Session.CurrentUser;
        db.AuditEvents.Add(new AuditEvent
        {
            UserId = user?.Id,
            UserName = user?.DisplayName ?? "Sistema",
            Operation = operation,
            Details = details,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static PosUser CreateUser(string username, string displayName, string pin, string role)
    {
        var user = new PosUser
        {
            Username = username,
            DisplayName = displayName,
            Role = role,
            IsActive = true
        };
        SetPin(user, pin);
        return user;
    }

    private static void SetPin(PosUser user, string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, PinIterations, HashAlgorithmName.SHA256, 32);
        user.PinSalt = Convert.ToBase64String(salt);
        user.PinHash = Convert.ToBase64String(hash);
    }

    private static decimal Money(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
