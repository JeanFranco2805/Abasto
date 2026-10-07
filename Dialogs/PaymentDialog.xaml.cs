using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Abasto.Domain;

namespace Abasto.Dialogs;

public partial class PaymentDialog : Window
{
    private readonly decimal _total;
    private readonly ObservableCollection<PaymentDraft> _payments = [];

    public IReadOnlyCollection<PaymentDraft> Payments => _payments.ToList();

    public PaymentDialog(decimal total)
    {
        InitializeComponent();
        _total = total;
        TotalText.Text = $"Total de la venta: {total:C0}";
        PaymentGrid.ItemsSource = _payments;
        AmountBox.Text = total.ToString("0.00", CultureInfo.CurrentCulture);
        RefreshRemaining();
    }

    private decimal Remaining => Math.Max(0m, _total - _payments.Sum(p => p.Amount));

    private void AddPayment_Click(object sender, RoutedEventArgs e)
    {
        var method = (PaymentMethodBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Efectivo";
        if (!TryParseMoney(AmountBox.Text, out var entered) || entered <= 0)
        {
            MessageBox.Show("Ingresa un valor válido mayor que cero.", "Revisar pago", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var remaining = Remaining;
        if (remaining <= 0)
            return;
        if (method != "Efectivo" && entered > remaining)
        {
            MessageBox.Show("El valor de este medio de pago no puede superar el saldo pendiente.", "Revisar pago", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var applied = Math.Min(entered, remaining);
        var change = method == "Efectivo" ? Math.Max(0m, entered - remaining) : 0m;
        _payments.Add(new PaymentDraft(method, applied, entered, change));
        AmountBox.Text = Remaining.ToString("0.00", CultureInfo.CurrentCulture);
        RefreshRemaining();
    }

    private void RefreshRemaining()
    {
        var remaining = Remaining;
        RemainingText.Text = remaining == 0 ? "Pago completo" : $"Pendiente: {remaining:C0}";
        RemainingText.Foreground = remaining == 0
            ? System.Windows.Media.Brushes.ForestGreen
            : System.Windows.Media.Brushes.Firebrick;
        CompleteButton.IsEnabled = remaining == 0 && _payments.Count > 0;
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (Remaining != 0)
            return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryParseMoney(string value, out decimal amount) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out amount)
        || decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
}
