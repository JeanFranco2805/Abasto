using System.IO;
using System.Globalization;
using System.Windows;
using SupermercadoPOS.Data;
using SupermercadoPOS.Dialogs;
using SupermercadoPOS.Services;

namespace SupermercadoPOS;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    public static string DatabasePath { get; private set; } = string.Empty;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var colombianCulture = (CultureInfo)CultureInfo.GetCultureInfo("es-CO").Clone();
        colombianCulture.NumberFormat.CurrencyDecimalDigits = 0;
        CultureInfo.DefaultThreadCurrentCulture = colombianCulture;
        CultureInfo.DefaultThreadCurrentUICulture = colombianCulture;
        CultureInfo.CurrentCulture = colombianCulture;
        CultureInfo.CurrentUICulture = colombianCulture;
        base.OnStartup(e);

        _instanceMutex = new Mutex(false, @"Local\SupermercadoPOS");
        try
        {
            _ownsInstanceMutex = _instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstanceMutex = true;
        }

        if (!_ownsInstanceMutex)
        {
            MessageBox.Show(
                "El punto de venta ya está abierto. Usa la ventana que está en ejecución.",
                "Supermercado POS",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SupermercadoPOS",
                "data");
            Directory.CreateDirectory(dataDirectory);
            DatabasePath = Path.Combine(dataDirectory, "pos.db");
            await PosService.InitializeAsync(DatabasePath);
            try
            {
                await PosService.CreateDailyBackupAsync(DatabasePath);
            }
            catch (Exception backupError)
            {
                MessageBox.Show(
                    $"El punto de venta continuará, pero no se pudo crear la copia de seguridad de hoy.\n\n{backupError.Message}",
                    "Aviso de copia de seguridad",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var login = new LoginWindow();
            if (login.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            // Keep explicit shutdown while transitioning from the modal login.
            // Closing the login window must not shut down the application before
            // the main POS window has finished opening.
            mainWindow.Closed += (_, _) => Shutdown();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No se pudo iniciar el punto de venta.\n\n{ex.Message}",
                "Error de inicio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    public static PosDbContext CreateDbContext() => PosDbContext.Create(DatabasePath);
}
