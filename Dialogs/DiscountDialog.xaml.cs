using System.Globalization;
using System.Windows;
using SupermercadoPOS.Domain;
using SupermercadoPOS.Services;

namespace SupermercadoPOS.Dialogs;

public partial class DiscountDialog : Window
{
    public decimal DiscountRate { get; private set; }
    public string ApprovedBy { get; private set; } = "";

    public DiscountDialog(decimal initialPercent = 5m)
    {
        InitializeComponent();
        PercentBox.Text = initialPercent.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private async void Authorize_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!decimal.TryParse(PercentBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var percent)
            && !decimal.TryParse(PercentBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out percent))
        {
            ErrorText.Text = "Ingresa un porcentaje válido.";
            return;
        }
        if (percent is < 0 or > 100)
        {
            ErrorText.Text = "El descuento debe estar entre 0 y 100.";
            return;
        }

        try
        {
            var user = await PosService.AuthenticateAsync(UsernameBox.Text, PinBox.Password);
            if (user is null || user.Role == "Cajero")
            {
                ErrorText.Text = "Usuario o PIN de supervisor incorrecto.";
                PinBox.Clear();
                PinBox.Focus();
                return;
            }
            DiscountRate = percent / 100m;
            ApprovedBy = percent == 0 ? "" : user.DisplayName;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
