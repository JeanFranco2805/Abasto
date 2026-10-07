using System.IO;
using System.Globalization;
using System.Windows;
using Abasto.Data;
using Abasto.Dialogs;
using Abasto.Services;

namespace Abasto;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private Mutex? _legacyInstanceMutex;
    private bool _ownsLegacyInstanceMutex;

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

        _instanceMutex = new Mutex(false, @"Local\Abasto");
        try
        {
            _ownsInstanceMutex = _instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstanceMutex = true;
        }

        _legacyInstanceMutex = new Mutex(false, @"Local\SupermercadoPOS");
        try
        {
            _ownsLegacyInstanceMutex = _legacyInstanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsLegacyInstanceMutex = true;
        }

        if (!_ownsInstanceMutex)
        {
            MessageBox.Show(
                "El punto de venta ya está abierto. Usa la ventana que está en ejecución.",
                "Abasto",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (!_ownsLegacyInstanceMutex)
        {
            MessageBox.Show(
                "Cierra la versión anterior del punto de venta antes de iniciar Abasto.",
                "Abasto",
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
            if (!await PosService.HasUsersAsync())
            {
                var setup = new InitialSetupWindow();
                if (setup.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
            }

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
        if (_ownsLegacyInstanceMutex)
            _legacyInstanceMutex?.ReleaseMutex();
        _legacyInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    public static PosDbContext CreateDbContext() => PosDbContext.Create(DatabasePath);
}
