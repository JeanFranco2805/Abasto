using System.Collections.ObjectModel;
using System.Windows;
using SupermercadoPOS.Services;

namespace SupermercadoPOS.Dialogs;

public partial class UnitManagerDialog : Window
{
    private readonly ObservableCollection<string> _units = [];
    private readonly string? _protectedUnit;

    public UnitManagerDialog(string? protectedUnit = null)
    {
        _protectedUnit = protectedUnit;
        InitializeComponent();
        UnitsList.ItemsSource = _units;
        Loaded += async (_, _) => await RefreshUnitsAsync();
    }

    private async Task RefreshUnitsAsync(string? selectUnit = null)
    {
        var units = await UnitCatalogService.GetUnitsAsync();
        _units.Clear();
        foreach (var unit in units)
            _units.Add(unit);

        UnitsList.SelectedItem = selectUnit is null ? null : _units.FirstOrDefault(
            unit => string.Equals(unit, selectUnit, StringComparison.OrdinalIgnoreCase));
        UpdateRemoveButton();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            var unit = await UnitCatalogService.AddUnitAsync(UnitCodeBox.Text);
            UnitCodeBox.Clear();
            await RefreshUnitsAsync(unit);
            UnitCodeBox.Focus();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            UnitCodeBox.Focus();
            UnitCodeBox.SelectAll();
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (UnitsList.SelectedItem is not string unit)
            return;

        ErrorText.Text = "";
        try
        {
            await UnitCatalogService.RemoveUnitAsync(unit);
            await RefreshUnitsAsync();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void UnitsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateRemoveButton();

    private void UpdateRemoveButton() =>
        RemoveUnitButton.IsEnabled = UnitsList.SelectedItem is string unit
            && !UnitCatalogService.IsBuiltIn(unit)
            && !string.Equals(unit, _protectedUnit, StringComparison.OrdinalIgnoreCase);

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
