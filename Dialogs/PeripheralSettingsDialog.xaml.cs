using System.Globalization;
using System.Windows;
using Abasto.Services;

namespace Abasto.Dialogs;

public partial class PeripheralSettingsDialog : Window
{
    private readonly WindowsEscPosPeripheralService _printer = new();
    private readonly SerialScaleService _scale = new();

    public PeripheralSettingsDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        var settings = await PeripheralSettingsService.LoadAsync();
        StoreNameBox.Text = settings.StoreName;
        PrinterNameBox.Text = settings.PrinterName;
        ScalePortBox.Text = settings.ScalePort;
        ScaleBaudBox.Text = settings.ScaleBaudRate.ToString(CultureInfo.InvariantCulture);
        await RefreshFiscalProviderStatusAsync();
    }

    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(ScaleBaudBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var baudRate))
            throw new InvalidOperationException("Escribe una velocidad serial válida.");
        await PeripheralSettingsService.SaveAsync(new PeripheralSettings(
            PrinterNameBox.Text.Trim(), ScalePortBox.Text.Trim(), baudRate)
        {
            StoreName = StoreNameBox.Text.Trim()
        });
        PrinterStatusText.Text = "Configuración guardada en esta caja.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            PrinterStatusText.Text = exception.Message;
        }
    }

    private async void PrintTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            await _printer.PrintTestAsync();
            PrinterStatusText.Text = "Trabajo enviado a la impresora ESC/POS.";
        }
        catch (Exception exception)
        {
            PrinterStatusText.Text = exception.Message;
        }
    }

    private async void OpenDrawer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            await _printer.OpenAsync();
            PrinterStatusText.Text = "Pulso de apertura enviado al cajón.";
        }
        catch (Exception exception)
        {
            PrinterStatusText.Text = exception.Message;
        }
    }

    private async void ReadScale_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            var weight = await _scale.ReadWeightAsync();
            ScaleStatusText.Text = $"Peso recibido: {weight:N3} kg";
        }
        catch (Exception exception)
        {
            ScaleStatusText.Text = exception.Message;
        }
    }

    private async void CheckFiscalProvider_Click(object sender, RoutedEventArgs e) => await RefreshFiscalProviderStatusAsync();

    private async Task RefreshFiscalProviderStatusAsync()
    {
        try
        {
            var result = await FiscalInvoicingClient.GetProviderStatusAsync();
            FiscalProviderStatusText.Text = result.Configured
                ? $"Proveedor disponible: {result.ProviderName}."
                : "Backend conectado; no hay proveedor fiscal configurado.";
        }
        catch (Exception exception)
        {
            FiscalProviderStatusText.Text = exception.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
