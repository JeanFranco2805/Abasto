using System.Runtime.InteropServices;
using System.Text;
using Abasto.Domain;

namespace Abasto.Services;

public sealed class WindowsEscPosPeripheralService : IReceiptPrinter, ICashDrawer
{
    private static readonly SemaphoreSlim SpoolLock = new(1, 1);

    public async Task PrintAsync(ReceiptData receipt, CancellationToken cancellationToken = default)
    {
        var settings = await PeripheralSettingsService.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
            throw new InvalidOperationException("Configura una impresora térmica ESC/POS en Periféricos.");
        var bytes = BuildReceipt(receipt);
        await PrintRawAsync(settings.PrinterName, bytes, cancellationToken);
    }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        var settings = await PeripheralSettingsService.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
            throw new InvalidOperationException("Configura una impresora ESC/POS para abrir el cajón portamonedas.");
        await PrintRawAsync(settings.PrinterName, [0x1B, 0x70, 0x00, 0x19, 0xFA], cancellationToken);
    }

    public async Task PrintTestAsync(CancellationToken cancellationToken = default)
    {
        var settings = await PeripheralSettingsService.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
            throw new InvalidOperationException("Escribe el nombre exacto de la impresora instalada en Windows.");
        var bytes = BuildReceipt(new ReceiptData(
            string.IsNullOrWhiteSpace(settings.StoreName) ? "ABASTO" : settings.StoreName,
            "PRUEBA", DateTime.Now, Environment.UserName, [], [], 0, 0, 0, 0));
        await PrintRawAsync(settings.PrinterName, bytes, cancellationToken);
    }

    public async Task PrintSaleAsync(Sale sale, CancellationToken cancellationToken = default)
    {
        var settings = await PeripheralSettingsService.LoadAsync();
        var receipt = new ReceiptData(
            string.IsNullOrWhiteSpace(settings.StoreName) ? "ABASTO" : settings.StoreName,
            sale.Id.ToString(),
            sale.CreatedAtUtc.ToLocalTime(),
            sale.CashierName,
            sale.Items.Select(item => new ReceiptLine(item.ProductName, item.Quantity, item.Unit,
                item.UnitPrice, item.LineTotal)).ToList(),
            sale.Payments.Select(payment => new ReceiptPayment(payment.Method, payment.Amount)).ToList(),
            sale.Subtotal,
            sale.TaxTotal,
            sale.Total,
            sale.Payments.Sum(payment => payment.Change));
        await PrintAsync(receipt, cancellationToken);
    }

    private static byte[] BuildReceipt(ReceiptData receipt)
    {
        var output = new List<byte>();
        output.AddRange([0x1B, 0x40, 0x1B, 0x61, 0x01, 0x1B, 0x45, 0x01]);
        AddLine(output, receipt.StoreName);
        output.AddRange([0x1B, 0x45, 0x00]);
        AddLine(output, "COMPROBANTE DE VENTA");
        AddLine(output, $"Venta: {receipt.TicketNumber}");
        AddLine(output, receipt.CreatedAt.ToString("dd/MM/yyyy HH:mm"));
        AddLine(output, $"Cajero: {receipt.Cashier}");
        output.AddRange([0x1B, 0x61, 0x00]);
        AddLine(output, new string('-', 32));
        foreach (var line in receipt.Lines)
        {
            AddLine(output, line.Description);
            AddLine(output, $"{line.Quantity:N3} {line.Unit} x {FormatMoney(line.UnitPrice)}  {FormatMoney(line.Total)}");
        }
        AddLine(output, new string('-', 32));
        AddLine(output, $"Subtotal: {FormatMoney(receipt.Subtotal)}");
        AddLine(output, $"Impuestos: {FormatMoney(receipt.Tax)}");
        output.AddRange([0x1B, 0x45, 0x01]);
        AddLine(output, $"TOTAL: {FormatMoney(receipt.Total)}");
        output.AddRange([0x1B, 0x45, 0x00]);
        foreach (var payment in receipt.Payments)
            AddLine(output, $"{payment.Method}: {FormatMoney(payment.Amount)}");
        AddLine(output, $"Cambio: {FormatMoney(receipt.Change)}");
        output.AddRange([0x1B, 0x61, 0x01]);
        AddLine(output, "Gracias por su compra");
        output.AddRange([0x0A, 0x0A, 0x0A, 0x1D, 0x56, 0x01]);
        return output.ToArray();
    }

    private static void AddLine(List<byte> output, string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        output.AddRange(Encoding.ASCII.GetBytes(builder.ToString()));
        output.Add(0x0A);
    }

    private static string FormatMoney(decimal value) => $"$ {value:N0}";

    private static async Task PrintRawAsync(string printerName, byte[] bytes, CancellationToken cancellationToken)
    {
        await SpoolLock.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() => SendToPrinter(printerName, bytes), cancellationToken);
        }
        finally
        {
            SpoolLock.Release();
        }
    }

    private static void SendToPrinter(string printerName, byte[] bytes)
    {
        if (!OpenPrinter(printerName, out var printer, IntPtr.Zero))
            throw new InvalidOperationException($"Windows no pudo abrir la impresora '{printerName}'. Verifica el nombre y la conexión.");
        var documentStarted = false;
        var pageStarted = false;
        try
        {
            var document = new DocumentInfo { DocumentName = "Abasto POS", DataType = "RAW" };
            if (StartDocPrinter(printer, 1, ref document) == 0)
                throw new InvalidOperationException("No se pudo iniciar el trabajo de impresión ESC/POS.");
            documentStarted = true;
            if (!StartPagePrinter(printer))
                throw new InvalidOperationException("No se pudo iniciar la página de impresión.");
            pageStarted = true;
            if (!WritePrinter(printer, bytes, bytes.Length, out var written) || written != bytes.Length)
                throw new InvalidOperationException("La impresora no recibió todos los datos del comprobante.");
        }
        finally
        {
            if (pageStarted)
                EndPagePrinter(printer);
            if (documentStarted)
                EndDocPrinter(printer);
            ClosePrinter(printer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocumentInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? DocumentName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr printer, int level, ref DocumentInfo documentInfo);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written);
}
