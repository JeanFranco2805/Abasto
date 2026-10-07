using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Abasto.Domain;

namespace Abasto.Dialogs;

public partial class SaleReturnDialog : Window
{
    private readonly ObservableCollection<ReturnableSaleLine> _lines = [];

    public IReadOnlyCollection<SaleReturnLineDraft> RequestedLines => _lines
        .Where(line => line.QuantityToReturn > 0)
        .Select(line => new SaleReturnLineDraft(line.SaleItemId, line.QuantityToReturn))
        .ToList();

    public string ReturnReason => ReasonBox.Text.Trim();
    public decimal EstimatedTotal => _lines.Sum(line => line.ReturnValue);
    public decimal RefundableCash { get; }

    public SaleReturnDialog(Sale sale)
    {
        InitializeComponent();
        var priorCashRefunds = sale.Returns.SelectMany(item => item.Payments)
            .Where(item => item.Method == "Efectivo").Sum(item => item.Amount);
        RefundableCash = Math.Max(0m, sale.Payments.Where(item => item.Method == "Efectivo").Sum(item => item.Amount) - priorCashRefunds);
        SaleSummaryText.Text = $"Venta #{sale.Id} · {sale.CreatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm} · Pagada: {sale.Total:C0} · Devuelta: {sale.ReturnedTotal:C0} · Efectivo disponible: {RefundableCash:C0}";
        foreach (var item in sale.Items.OrderBy(item => item.ProductName))
        {
            var line = new ReturnableSaleLine(item);
            line.PropertyChanged += (_, _) => RefreshTotal();
            _lines.Add(line);
        }
        ReturnItemsGrid.ItemsSource = _lines;
        RefreshTotal();
    }

    private void ReturnItemsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(RefreshTotal, DispatcherPriority.Background);

    private void RefreshTotal() => ReturnTotalText.Text = EstimatedTotal.ToString("C0");

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        ReturnItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ReturnItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (RequestedLines.Count == 0)
        {
            ErrorText.Text = "Selecciona al menos una cantidad a devolver.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ReturnReason))
        {
            ErrorText.Text = "Escribe el motivo de la devolución.";
            ReasonBox.Focus();
            return;
        }
        if (EstimatedTotal > RefundableCash)
        {
            ErrorText.Text = $"El efectivo pendiente de devolución alcanza hasta {RefundableCash:C0}. Para otros medios de pago se necesita el adaptador del datáfono.";
            return;
        }
        var invalid = _lines.FirstOrDefault(line => line.QuantityToReturn < 0 || line.QuantityToReturn > line.AvailableQuantity);
        if (invalid is not null)
        {
            ErrorText.Text = $"La cantidad de {invalid.ProductName} excede la disponible para devolver.";
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class ReturnableSaleLine : INotifyPropertyChanged
{
    private decimal _quantityToReturn;

    public long SaleItemId { get; }
    public string ProductName { get; }
    public string Unit { get; }
    public decimal SoldQuantity { get; }
    public decimal ReturnedQuantity { get; }
    public decimal AvailableQuantity => SoldQuantity - ReturnedQuantity;
    public decimal LineTotal { get; }
    public decimal AlreadyReturnedAmount { get; }

    public decimal QuantityToReturn
    {
        get => _quantityToReturn;
        set
        {
            if (_quantityToReturn == value)
                return;
            _quantityToReturn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReturnValue));
        }
    }

    public decimal ReturnValue => QuantityToReturn <= 0 || SoldQuantity <= 0
        ? 0m
        : QuantityToReturn == AvailableQuantity
            ? LineTotal - AlreadyReturnedAmount
            : decimal.Round(LineTotal / SoldQuantity * QuantityToReturn, 2, MidpointRounding.AwayFromZero);

    public event PropertyChangedEventHandler? PropertyChanged;

    public ReturnableSaleLine(SaleItem item)
    {
        SaleItemId = item.Id;
        ProductName = item.ProductName;
        Unit = item.Unit;
        SoldQuantity = item.Quantity;
        ReturnedQuantity = item.ReturnedQuantity;
        LineTotal = item.LineTotal;
        AlreadyReturnedAmount = item.ReturnedAmount;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
