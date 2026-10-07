using System.Windows;
using Abasto.Domain;
using Abasto.Services;

namespace Abasto.Dialogs;

public partial class SuspendedSaleDialog : Window
{
    public Sale? SelectedSale => SalesGrid.SelectedItem as Sale;

    public SuspendedSaleDialog()
    {
        InitializeComponent();
        Loaded += LoadSalesAsync;
    }

    private async void LoadSalesAsync(object sender, RoutedEventArgs e)
    {
        SalesGrid.ItemsSource = await PosService.GetSuspendedSalesAsync();
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSale is null)
        {
            MessageBox.Show("Selecciona una venta.", "Reanudar venta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
