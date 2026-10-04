using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace SupermercadoPOS.Dialogs;

public enum ShiftAction
{
    Open,
    CashIn,
    CashOut,
    Close
}

public partial class ShiftDialog : Window
{
    private readonly bool _hasOpenShift;
    private readonly decimal _expectedCash;

    public ShiftAction SelectedAction { get; private set; }
    public decimal Amount { get; private set; }
    public string Reason { get; private set; } = "";

    public ShiftDialog(bool hasOpenShift, decimal expectedCash = 0)
    {
        InitializeComponent();
        _hasOpenShift = hasOpenShift;
        _expectedCash = expectedCash;
        ModeText.Text = hasOpenShift ? "Administra el efectivo o realiza el cierre y arqueo." : "Abre un turno con el fondo inicial de caja.";
        if (hasOpenShift)
        {
            ActionBox.Items.Add("Entrada de efectivo");
            ActionBox.Items.Add("Salida de efectivo");
            ActionBox.Items.Add("Cerrar turno");
            ActionBox.SelectedIndex = 0;
            ExpectedText.Text = $"Efectivo esperado en caja: {expectedCash:C0}";
        }
        else
        {
            ActionBox.Items.Add("Abrir turno");
            ActionBox.SelectedIndex = 0;
            ActionBox.IsEnabled = false;
            ExpectedText.Text = "El fondo inicial se registra como parte del arqueo.";
        }
        RefreshFields();
    }

    private void Action_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            RefreshFields();
    }

    private void RefreshFields()
    {
        var action = ActionBox.SelectedItem?.ToString() ?? "";
        if (!_hasOpenShift)
        {
            SelectedAction = ShiftAction.Open;
            AmountLabel.Text = "Fondo inicial";
            ReasonBox.IsEnabled = false;
            ExpectedText.Visibility = Visibility.Visible;
        }
        else if (action == "Entrada de efectivo")
        {
            SelectedAction = ShiftAction.CashIn;
            AmountLabel.Text = "Valor de la entrada";
            ReasonBox.IsEnabled = true;
            ExpectedText.Visibility = Visibility.Collapsed;
        }
        else if (action == "Salida de efectivo")
        {
            SelectedAction = ShiftAction.CashOut;
            AmountLabel.Text = "Valor de la salida";
            ReasonBox.IsEnabled = true;
            ExpectedText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SelectedAction = ShiftAction.Close;
            AmountLabel.Text = "Efectivo contado";
            ReasonBox.IsEnabled = false;
            ExpectedText.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(AmountBox.Text) || AmountBox.Text == "0")
                AmountBox.Text = _expectedCash.ToString("0.00", CultureInfo.CurrentCulture);
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!decimal.TryParse(AmountBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount)
            && !decimal.TryParse(AmountBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
        {
            ErrorText.Text = "Ingresa un valor numérico.";
            return;
        }
        if (amount < 0 || (SelectedAction is ShiftAction.CashIn or ShiftAction.CashOut && amount == 0))
        {
            ErrorText.Text = "El valor debe ser válido y mayor que cero para un movimiento.";
            return;
        }
        if (SelectedAction is ShiftAction.CashIn or ShiftAction.CashOut && string.IsNullOrWhiteSpace(ReasonBox.Text))
        {
            ErrorText.Text = "Escribe el motivo del movimiento.";
            return;
        }

        Amount = amount;
        Reason = ReasonBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
