using System.Windows;
using System.Windows.Controls;
using SupermercadoPOS.Domain;

namespace SupermercadoPOS.Dialogs;

public partial class UserAdminDialog : Window
{
    private readonly PosUserSummary? _user;

    public string Username => UsernameBox.Text.Trim();
    public string DisplayName => DisplayNameBox.Text.Trim();
    public string Role => (RoleBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Cajero";
    public string Pin => PinBox.Password;

    public UserAdminDialog(PosUserSummary? user = null)
    {
        InitializeComponent();
        _user = user;
        if (user is null)
        {
            PinFields.Visibility = Visibility.Visible;
            UserFields.Visibility = Visibility.Visible;
            return;
        }

        Title = "Restablecer PIN";
        HeadingText.Text = "Restablecer PIN";
        SubtitleText.Text = "El usuario deberá usar el nuevo PIN en su próximo acceso.";
        TargetBorder.Visibility = Visibility.Visible;
        TargetText.Text = $"{user.DisplayName} · {user.Username} · {user.Role}";
        UserFields.Visibility = Visibility.Collapsed;
        PinFields.Visibility = Visibility.Visible;
        SaveButton.Content = "Guardar PIN";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (_user is null && (Username.Length < 3 || string.IsNullOrWhiteSpace(DisplayName)))
        {
            ErrorText.Text = "Completa el nombre visible y un usuario de al menos 3 caracteres.";
            return;
        }
        if (PinBox.Password.Length is < 4 or > 12 || PinBox.Password.Any(character => !char.IsDigit(character)))
        {
            ErrorText.Text = "El PIN debe contener entre 4 y 12 dígitos.";
            return;
        }
        if (PinBox.Password != ConfirmPinBox.Password)
        {
            ErrorText.Text = "Los PIN no coinciden.";
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
