using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Abasto.Data;
using Abasto.Domain;

namespace Abasto.Services;

public static class PosService
{
    private const int PinIterations = 120_000;
    private static readonly SemaphoreSlim BackupLock = new(1, 1);
    private static readonly string[] LegacySampleProductBarcodes =
    [
        "7701001000011", "7701001000012", "7701001000013", "7701001000014",
        "7701001000015", "7701001000016", "7701001000017", "7701001000018"
    ];

    public static async Task InitializeAsync(string databasePath)
    {
        if (File.Exists(databasePath))
            await CreateDailyBackupAsync(databasePath);
        await using var db = PosDbContext.Create(databasePath);
        await db.Database.EnsureCreatedAsync();
        await EnsureProductImageColumnAsync(db);
        await EnsureReturnAndFiscalSchemaAsync(db);

        await RemoveLegacySampleDataAsync(db);

        var hasProductSnapshot = await db.SyncQueue.AnyAsync(item => item.EntityType == "Product"
            && (item.Operation == "ProductSnapshot" || item.Operation == "ProductCreated" || item.Operation == "ProductUpdated"));
        if (!hasProductSnapshot)
        {
            var products = await db.Products.AsNoTracking().ToListAsync();
            foreach (var product in products)
                db.SyncQueue.Add(CreateSyncEvent("Product", product.Id.ToString(), "ProductSnapshot", ProductPayload(product)));
            await db.SaveChangesAsync();
        }
    }

    public static async Task<bool> HasUsersAsync()
    {
        await using var db = App.CreateDbContext();
        return await db.Users.AnyAsync();
    }

    public static async Task CreateInitialAdministratorAsync(string username, string displayName, string pin)
    {
        username = username.Trim().ToLowerInvariant();
        displayName = displayName.Trim();
        ValidateUserInput(username, displayName, "Administrador", pin);

        await using var db = App.CreateDbContext();
        if (await db.Users.AnyAsync())
            throw new InvalidOperationException("Ya existe una cuenta. Inicia sesión para continuar.");

        db.Users.Add(CreateUser(username, displayName, pin, "Administrador"));
        await db.SaveChangesAsync();
    }

    private static async Task RemoveLegacySampleDataAsync(PosDbContext db)
    {
        var demoProducts = await db.Products
            .Where(product => LegacySampleProductBarcodes.Contains(product.Barcode))
            .ToListAsync();
        if (demoProducts.Count > 0)
        {
            var demoProductIds = demoProducts.Select(product => product.Id.ToString()).ToArray();
            var demoEvents = await db.SyncQueue
                .Where(item => item.EntityType == "Product" && demoProductIds.Contains(item.EntityId))
                .ToListAsync();
            db.SyncQueue.RemoveRange(demoEvents);
            db.Products.RemoveRange(demoProducts);
        }

        var demoUsers = await db.Users.Where(user =>
            (user.Username == "cajero" && user.DisplayName == "Cajero de demostración")
            || (user.Username == "supervisor" && user.DisplayName == "Supervisor de demostración"))
            .ToListAsync();
        db.Users.RemoveRange(demoUsers);

        var demoAdmin = await db.Users.FirstOrDefaultAsync(user =>
            user.Username == "admin" && user.DisplayName == "Administrador de demostración");
        if (demoAdmin is not null)
            demoAdmin.DisplayName = "Administrador";

        if (demoProducts.Count > 0 || demoUsers.Count > 0 || demoAdmin is not null)
            await db.SaveChangesAsync();
    }

    private static async Task EnsureProductImageColumnAsync(PosDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            var connection = db.Database.GetDbConnection();
            var hasImagePath = false;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info('Products');";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (string.Equals(reader.GetString(1), "ImagePath", StringComparison.OrdinalIgnoreCase))
                    {
                        hasImagePath = true;
                        break;
                    }
                }
            }

            if (!hasImagePath)
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Products\" ADD COLUMN \"ImagePath\" TEXT NOT NULL DEFAULT '';");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureReturnAndFiscalSchemaAsync(PosDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await EnsureColumnAsync(db, "Sales", "ReturnedTotal");
            await EnsureColumnAsync(db, "SaleItems", "ReturnedQuantity");
            await EnsureColumnAsync(db, "SaleItems", "ReturnedAmount");
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "SaleReturns" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SaleReturns" PRIMARY KEY AUTOINCREMENT,
                    "SaleId" INTEGER NOT NULL,
                    "CreatedAtUtc" TEXT NOT NULL,
                    "CashierId" INTEGER NOT NULL,
                    "CashierName" TEXT NOT NULL,
                    "SupervisorId" INTEGER NOT NULL,
                    "SupervisorName" TEXT NOT NULL,
                    "Reason" TEXT NOT NULL,
                    "Total" TEXT NOT NULL,
                    CONSTRAINT "FK_SaleReturns_Sales_SaleId" FOREIGN KEY ("SaleId") REFERENCES "Sales" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_SaleReturns_SaleId_CreatedAtUtc" ON "SaleReturns" ("SaleId", "CreatedAtUtc");
                CREATE TABLE IF NOT EXISTS "SaleReturnItems" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SaleReturnItems" PRIMARY KEY AUTOINCREMENT,
                    "SaleReturnId" INTEGER NOT NULL,
                    "SaleItemId" INTEGER NOT NULL,
                    "ProductId" INTEGER NOT NULL,
                    "ProductName" TEXT NOT NULL,
                    "Unit" TEXT NOT NULL,
                    "Quantity" TEXT NOT NULL,
                    "Amount" TEXT NOT NULL,
                    CONSTRAINT "FK_SaleReturnItems_SaleReturns_SaleReturnId" FOREIGN KEY ("SaleReturnId") REFERENCES "SaleReturns" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_SaleReturnItems_SaleReturnId" ON "SaleReturnItems" ("SaleReturnId");
                CREATE TABLE IF NOT EXISTS "SaleReturnPayments" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SaleReturnPayments" PRIMARY KEY AUTOINCREMENT,
                    "SaleReturnId" INTEGER NOT NULL,
                    "OriginalPaymentId" INTEGER NOT NULL,
                    "Method" TEXT NOT NULL,
                    "Amount" TEXT NOT NULL,
                    "ExternalReference" TEXT NULL,
                    CONSTRAINT "FK_SaleReturnPayments_SaleReturns_SaleReturnId" FOREIGN KEY ("SaleReturnId") REFERENCES "SaleReturns" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_SaleReturnPayments_SaleReturnId" ON "SaleReturnPayments" ("SaleReturnId");
                CREATE TABLE IF NOT EXISTS "FiscalDocuments" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_FiscalDocuments" PRIMARY KEY AUTOINCREMENT,
                    "SaleId" INTEGER NOT NULL,
                    "SaleReturnId" INTEGER NULL,
                    "Kind" TEXT NOT NULL,
                    "Status" TEXT NOT NULL,
                    "Provider" TEXT NOT NULL,
                    "DocumentNumber" TEXT NOT NULL,
                    "ProviderDocumentId" TEXT NOT NULL,
                    "ResponsePayload" TEXT NOT NULL,
                    "Error" TEXT NOT NULL,
                    "CreatedAtUtc" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_FiscalDocuments_SaleId_Kind_Status" ON "FiscalDocuments" ("SaleId", "Kind", "Status");
                """);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureColumnAsync(PosDbContext db, string table, string column)
    {
        var found = false;
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
        }

        if (found)
            return;
        var sql = (table, column) switch
        {
            ("Sales", "ReturnedTotal") => "ALTER TABLE \"Sales\" ADD COLUMN \"ReturnedTotal\" TEXT NOT NULL DEFAULT '0';",
            ("SaleItems", "ReturnedQuantity") => "ALTER TABLE \"SaleItems\" ADD COLUMN \"ReturnedQuantity\" TEXT NOT NULL DEFAULT '0';",
            ("SaleItems", "ReturnedAmount") => "ALTER TABLE \"SaleItems\" ADD COLUMN \"ReturnedAmount\" TEXT NOT NULL DEFAULT '0';",
            _ => throw new InvalidOperationException("No se reconoce la actualización de base de datos solicitada.")
        };
        await db.Database.ExecuteSqlRawAsync(sql);
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
            db.SyncQueue.Add(CreateSyncEvent("Product", product.Id.ToString(), "ProductCreated", ProductPayload(product)));
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
            stored.ImagePath = product.ImagePath;
            stored.UnitPrice = product.UnitPrice;
            stored.TaxRate = product.TaxRate;
            stored.Stock = product.Stock;
            stored.MinimumStock = product.MinimumStock;
            stored.IsActive = product.IsActive;
            stored.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            db.SyncQueue.Add(CreateSyncEvent("Product", stored.Id.ToString(), "ProductUpdated", ProductPayload(stored)));
            await AddAuditAsync(db, "Producto actualizado", $"{stored.Name} ({stored.Barcode})");
        }
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task<bool> IsProductUnitInUseAsync(string unit)
    {
        await using var db = App.CreateDbContext();
        return await db.Products.AnyAsync(product => product.Unit == unit)
            || await db.SaleItems.AnyAsync(item => item.Unit == unit);
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
        db.SyncQueue.Add(CreateSyncEvent("CashShift", shift.Id.ToString(), "CashShiftOpened", shift));
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
        var movement = new CashMovement
        {
            CashShiftId = shift.Id,
            UserId = user.Id,
            UserName = user.DisplayName,
            IsCashIn = isCashIn,
            Amount = Money(amount),
            Reason = reason.Trim()
        };
        db.CashMovements.Add(movement);
        await db.SaveChangesAsync();
        db.SyncQueue.Add(CreateSyncEvent("CashMovement", movement.Id.ToString(), "CashMovementRegistered", movement));
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
        db.SyncQueue.Add(CreateSyncEvent("CashShift", shift.Id.ToString(), "CashShiftClosed", shift));
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
            db.SyncQueue.Add(CreateSyncEvent("Product", product.Id.ToString(), "ProductStockUpdated", ProductPayload(product)));
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
        db.SyncQueue.Add(CreateSyncEvent("Sale", sale.Id.ToString(), "SaleCompleted", SalePayload(sale)));
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
            .Include(s => s.Returns)
                .ThenInclude(item => item.Payments)
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
            db.SyncQueue.Add(CreateSyncEvent("Product", product.Id.ToString(), "ProductStockUpdated", ProductPayload(product)));
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
        db.SyncQueue.Add(CreateSyncEvent("Sale", sale.Id.ToString(), "SaleVoided", SalePayload(sale)));
        await AddAuditAsync(db, "Venta anulada",
            $"Venta #{sale.Id}; devolución en efectivo: {refund:C0}; autorizó {supervisor.DisplayName}");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task<SaleReturn> ReturnSaleItemsAsync(
        long saleId,
        IReadOnlyCollection<SaleReturnLineDraft> requestedLines,
        PosUser supervisor,
        string reason)
    {
        var cashier = RequireUser();
        reason = reason.Trim();
        if (requestedLines.Count == 0)
            throw new InvalidOperationException("Selecciona al menos un producto y una cantidad para devolver.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Escribe el motivo de la devolución.");
        if (supervisor.Role == "Cajero")
            throw new InvalidOperationException("Se requiere autorización de supervisor.");

        await using var db = App.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var verifiedSupervisor = await db.Users.AsNoTracking()
            .AnyAsync(user => user.Id == supervisor.Id && user.IsActive && user.Role != "Cajero");
        if (!verifiedSupervisor)
            throw new InvalidOperationException("La autorización de supervisor ya no es válida.");

        var sale = await db.Sales
            .Include(item => item.Items)
            .Include(item => item.Payments)
            .Include(item => item.Returns)
                .ThenInclude(item => item.Payments)
            .FirstOrDefaultAsync(item => item.Id == saleId
                && (item.Status == "Completada" || item.Status == "Devuelta parcialmente"))
            ?? throw new InvalidOperationException("La venta no existe o ya fue devuelta por completo.");
        var currentShift = await db.CashShifts.FirstOrDefaultAsync(shift =>
            shift.CashierId == cashier.Id && shift.Status == "Abierto")
            ?? throw new InvalidOperationException("Abre un turno antes de entregar una devolución en efectivo.");

        if (requestedLines.GroupBy(item => item.SaleItemId).Any(group => group.Count() > 1))
            throw new InvalidOperationException("La devolución contiene productos repetidos.");

        var returnRecord = new SaleReturn
        {
            SaleId = sale.Id,
            CashierId = cashier.Id,
            CashierName = cashier.DisplayName,
            SupervisorId = supervisor.Id,
            SupervisorName = supervisor.DisplayName,
            Reason = reason,
            CreatedAtUtc = DateTime.UtcNow
        };

        foreach (var draft in requestedLines)
        {
            if (draft.Quantity <= 0)
                throw new InvalidOperationException("La cantidad a devolver debe ser mayor que cero.");
            var soldItem = sale.Items.FirstOrDefault(item => item.Id == draft.SaleItemId)
                ?? throw new InvalidOperationException("Uno de los productos no pertenece a esta venta.");
            var remainingQuantity = soldItem.Quantity - soldItem.ReturnedQuantity;
            if (draft.Quantity > remainingQuantity)
                throw new InvalidOperationException($"Solo quedan {remainingQuantity:N3} {soldItem.Unit} disponibles para devolver de {soldItem.ProductName}.");

            var amount = draft.Quantity == remainingQuantity
                ? Money(soldItem.LineTotal - soldItem.ReturnedAmount)
                : Money(soldItem.LineTotal / soldItem.Quantity * draft.Quantity);
            if (amount <= 0)
                throw new InvalidOperationException("El valor de devolución calculado no es válido.");

            soldItem.ReturnedQuantity += draft.Quantity;
            soldItem.ReturnedAmount += amount;
            returnRecord.Items.Add(new SaleReturnItem
            {
                SaleItemId = soldItem.Id,
                ProductId = soldItem.ProductId,
                ProductName = soldItem.ProductName,
                Unit = soldItem.Unit,
                Quantity = draft.Quantity,
                Amount = amount
            });

            var product = await db.Products.FirstOrDefaultAsync(item => item.Id == soldItem.ProductId)
                ?? throw new InvalidOperationException($"No se encontró el producto {soldItem.ProductName} para reponer inventario.");
            product.Stock += draft.Quantity;
            product.UpdatedAtUtc = DateTime.UtcNow;
            db.SyncQueue.Add(CreateSyncEvent("Product", product.Id.ToString(), "ProductStockUpdated", ProductPayload(product)));
        }

        returnRecord.Total = Money(returnRecord.Items.Sum(item => item.Amount));
        var previouslyRefundedCash = sale.Returns.SelectMany(item => item.Payments)
            .Where(item => item.Method == "Efectivo")
            .GroupBy(item => item.OriginalPaymentId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Amount));
        var remainingRefund = returnRecord.Total;
        foreach (var payment in sale.Payments.Where(item => item.Method == "Efectivo").OrderBy(item => item.Id))
        {
            var alreadyRefunded = previouslyRefundedCash.GetValueOrDefault(payment.Id);
            var available = Money(payment.Amount - alreadyRefunded);
            var amount = Math.Min(available, remainingRefund);
            if (amount <= 0)
                continue;
            returnRecord.Payments.Add(new SaleReturnPayment
            {
                OriginalPaymentId = payment.Id,
                Method = payment.Method,
                Amount = amount,
                ExternalReference = payment.ExternalReference
            });
            remainingRefund = Money(remainingRefund - amount);
            if (remainingRefund == 0)
                break;
        }

        if (remainingRefund > 0)
            throw new InvalidOperationException("La venta no tiene suficiente pago en efectivo pendiente de devolución. Para devolver tarjeta o transferencia se debe conectar el datáfono.");

        sale.ReturnedTotal = Money(sale.ReturnedTotal + returnRecord.Total);
        sale.Status = sale.Items.All(item => item.ReturnedQuantity >= item.Quantity) ? "Devuelta" : "Devuelta parcialmente";
        db.SaleReturns.Add(returnRecord);
        db.CashMovements.Add(new CashMovement
        {
            CashShiftId = currentShift.Id,
            UserId = supervisor.Id,
            UserName = supervisor.DisplayName,
            IsCashIn = false,
            Amount = returnRecord.Total,
            Reason = $"Devolución de venta #{sale.Id}"
        });
        await db.SaveChangesAsync();

        db.SyncQueue.Add(CreateSyncEvent("Sale", sale.Id.ToString(), "SaleReturned", SalePayload(sale)));
        db.SyncQueue.Add(CreateSyncEvent("SaleReturn", returnRecord.Id.ToString(), "SaleReturnCompleted", SaleReturnPayload(returnRecord)));
        await AddAuditAsync(db, "Devolución de venta",
            $"Venta #{sale.Id}; devolución {returnRecord.Total:C0}; motivo: {reason}; autorizó {supervisor.DisplayName}");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await CreateDailyBackupAsync(App.DatabasePath);
        return returnRecord;
    }

    public static async Task<List<SaleReturn>> GetSaleReturnsAsync(long saleId)
    {
        await using var db = App.CreateDbContext();
        return await db.SaleReturns.AsNoTracking()
            .Include(item => item.Items)
            .Include(item => item.Payments)
            .Where(item => item.SaleId == saleId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToListAsync();
    }

    public static async Task SaveFiscalDocumentAsync(FiscalDocument document)
    {
        await using var db = App.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.FiscalDocuments.Add(document);
        await db.SaveChangesAsync();
        db.SyncQueue.Add(CreateSyncEvent("FiscalDocument", document.Id.ToString(),
            document.Status == "Emitida" ? "FiscalDocumentIssued" : "FiscalDocumentFailed", document));
        var operation = document.Status == "Emitida" ? "emitida" : "fallida";
        await AddAuditAsync(db, document.Kind == "Factura" ? $"Factura electrónica {operation}" : $"Nota crédito {operation}",
            document.Status == "Emitida"
                ? $"Venta #{document.SaleId}; documento {document.DocumentNumber}; proveedor {document.Provider}"
                : $"Venta #{document.SaleId}; error: {document.Error}");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await CreateDailyBackupAsync(App.DatabasePath);
    }

    public static async Task<List<FiscalDocument>> GetFiscalDocumentsAsync(long saleId)
    {
        await using var db = App.CreateDbContext();
        return await db.FiscalDocuments.AsNoTracking()
            .Where(item => item.SaleId == saleId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToListAsync();
    }

    public static async Task<SalesSummary> GetTodaySummaryAsync()
    {
        var localStart = DateTime.Today;
        var startUtc = localStart.ToUniversalTime();
        var endUtc = localStart.AddDays(1).ToUniversalTime();
        await using var db = App.CreateDbContext();
        var sales = await db.Sales.AsNoTracking()
            .Include(s => s.Payments)
            .Where(s => (s.Status == "Completada" || s.Status == "Devuelta parcialmente")
                && s.CreatedAtUtc >= startUtc && s.CreatedAtUtc < endUtc)
            .ToListAsync();
        var payments = sales.SelectMany(s => s.Payments).ToList();
        return new SalesSummary(
            sales.Count,
            sales.Sum(s => s.Total - s.ReturnedTotal),
            payments.Where(p => p.Method == "Efectivo").Sum(p => p.Amount) - sales.Sum(s => s.ReturnedTotal),
            payments.Where(p => p.Method == "Tarjeta").Sum(p => p.Amount),
            payments.Where(p => p.Method is not ("Efectivo" or "Tarjeta")).Sum(p => p.Amount));
    }

    public static async Task<List<Sale>> GetRecentSalesAsync(int count = 15)
    {
        await using var db = App.CreateDbContext();
        return await db.Sales.AsNoTracking()
            .Include(s => s.Payments)
            .Where(s => s.Status == "Completada" || s.Status == "Devuelta parcialmente")
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
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .Where(s => (s.Status == "Completada" || s.Status == "Devuelta parcialmente" || s.Status == "Devuelta" || s.Status == "Anulada")
                && s.CreatedAtUtc >= startUtc && s.CreatedAtUtc < endUtc)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync();
    }

    public static async Task<List<Sale>> GetPendingSalesBetweenAsync(DateTime fromLocal, DateTime toLocal)
    {
        var startUtc = fromLocal.Date.ToUniversalTime();
        var endUtc = toLocal.Date.AddDays(1).ToUniversalTime();
        await using var db = App.CreateDbContext();
        var pendingIds = await db.SyncQueue.AsNoTracking()
            .Where(item => item.EntityType == "Sale" && item.Status == "Pendiente")
            .Select(item => item.EntityId)
            .Distinct()
            .ToListAsync();
        var saleIds = pendingIds.Select(id => long.TryParse(id, out var parsed) ? parsed : 0)
            .Where(id => id > 0).Distinct().ToArray();
        if (saleIds.Length == 0)
            return [];
        return await db.Sales.AsNoTracking()
            .Include(sale => sale.Items)
            .Include(sale => sale.Payments)
            .Where(sale => saleIds.Contains(sale.Id)
                && (sale.Status == "Completada" || sale.Status == "Devuelta parcialmente" || sale.Status == "Devuelta" || sale.Status == "Anulada")
                && sale.CreatedAtUtc >= startUtc && sale.CreatedAtUtc < endUtc)
            .ToListAsync();
    }

    public static async Task<List<Product>> MergeServerProductsAsync(IReadOnlyCollection<Product> serverProducts)
    {
        await using var db = App.CreateDbContext();
        var pendingIdsText = await db.SyncQueue.AsNoTracking()
            .Where(item => item.EntityType == "Product" && item.Status == "Pendiente")
            .Select(item => item.EntityId)
            .Distinct()
            .ToListAsync();
        var pendingIds = pendingIdsText.Select(id => int.TryParse(id, out var parsed) ? parsed : 0)
            .Where(id => id > 0).ToHashSet();
        var localProducts = await db.Products.ToListAsync();
        var byId = localProducts.ToDictionary(product => product.Id);
        var byBarcode = localProducts.ToDictionary(product => product.Barcode, StringComparer.Ordinal);

        foreach (var remote in serverProducts)
        {
            if (pendingIds.Contains(remote.Id))
                continue;

            if (!byId.TryGetValue(remote.Id, out var local)
                && !byBarcode.TryGetValue(remote.Barcode, out local))
            {
                local = new Product { Id = remote.Id, Barcode = remote.Barcode };
                db.Products.Add(local);
                localProducts.Add(local);
                byId[local.Id] = local;
                byBarcode[local.Barcode] = local;
            }

            local.Barcode = remote.Barcode;
            local.Name = remote.Name;
            local.Category = remote.Category;
            local.Unit = remote.Unit;
            local.UnitPrice = remote.UnitPrice;
            local.TaxRate = remote.TaxRate;
            local.Stock = remote.Stock;
            local.MinimumStock = remote.MinimumStock;
            local.IsActive = remote.IsActive;
            local.UpdatedAtUtc = remote.UpdatedAtUtc;
        }

        await db.SaveChangesAsync();
        var serverIds = serverProducts.Select(product => product.Id).ToHashSet();
        return localProducts.Where(product => product.IsActive
                && (serverIds.Contains(product.Id) || pendingIds.Contains(product.Id)))
            .OrderBy(product => product.Name).ToList();
    }

    public static async Task<SalesSummary> GetSalesSummaryBetweenAsync(DateTime fromLocal, DateTime toLocal)
    {
        var sales = await GetSalesBetweenAsync(fromLocal, toLocal);
        var completed = sales.Where(s => s.Status == "Completada" || s.Status == "Devuelta parcialmente").ToList();
        var payments = completed.SelectMany(s => s.Payments).ToList();
        return new SalesSummary(
            completed.Count,
            completed.Sum(s => s.Total - s.ReturnedTotal),
            payments.Where(p => p.Method == "Efectivo").Sum(p => p.Amount) - completed.Sum(s => s.ReturnedTotal),
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
            .Where(i => i.Sale != null && (i.Sale.Status == "Completada" || i.Sale.Status == "Devuelta parcialmente")
                && i.Sale.CreatedAtUtc >= startUtc && i.Sale.CreatedAtUtc < endUtc)
            .ToListAsync();
        var totals = items.GroupBy(i => i.ProductId)
            .ToDictionary(group => group.Key, group => (
                Quantity: group.Sum(i => i.Quantity - i.ReturnedQuantity),
                Revenue: group.Sum(i => i.LineTotal - i.ReturnedAmount)));
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

    private static SyncQueueItem CreateSyncEvent(string entityType, string entityId, string operation, object payload) => new()
    {
        EntityType = entityType,
        EntityId = entityId,
        Operation = operation,
        Payload = JsonSerializer.Serialize(payload)
    };

    private static object ProductPayload(Product product) => new
    {
        product.Id,
        product.Barcode,
        product.Name,
        product.Category,
        product.Unit,
        product.UnitPrice,
        product.TaxRate,
        product.Stock,
        product.MinimumStock,
        product.IsActive,
        product.UpdatedAtUtc
    };

    private static object SalePayload(Sale sale) => new
    {
        sale.Id,
        sale.CreatedAtUtc,
        sale.Status,
        sale.CashierId,
        sale.CashierName,
        sale.CashShiftId,
        sale.CustomerName,
        sale.CustomerDocument,
        sale.Subtotal,
        sale.DiscountTotal,
        sale.TaxTotal,
        sale.Total,
        sale.ReturnedTotal,
        Items = sale.Items.Select(item => new
        {
            item.ProductId,
            item.Barcode,
            item.ProductName,
            item.Unit,
            item.Quantity,
            item.UnitPrice,
            item.TaxRate,
            item.DiscountApprovedBy,
            item.DiscountAmount,
            item.LineSubtotal,
            item.LineTax,
            item.LineTotal,
            item.ReturnedQuantity,
            item.ReturnedAmount
        }),
        Payments = sale.Payments.Select(payment => new { payment.Method, payment.Amount, payment.Tendered, payment.Change, payment.ExternalReference })
    };

    private static object SaleReturnPayload(SaleReturn saleReturn) => new
    {
        saleReturn.Id,
        saleReturn.SaleId,
        saleReturn.CreatedAtUtc,
        saleReturn.CashierId,
        saleReturn.CashierName,
        saleReturn.SupervisorId,
        saleReturn.SupervisorName,
        saleReturn.Reason,
        saleReturn.Total,
        Items = saleReturn.Items.Select(item => new
        {
            item.SaleItemId,
            item.ProductId,
            item.ProductName,
            item.Unit,
            item.Quantity,
            item.Amount
        }),
        Payments = saleReturn.Payments.Select(payment => new
        {
            payment.OriginalPaymentId,
            payment.Method,
            payment.Amount,
            payment.ExternalReference
        })
    };

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
