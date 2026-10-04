using System.Windows;
using SupermercadoPOS.Services;

namespace SupermercadoPOS.Dialogs;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PinBox.Focus();
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            var user = await PosService.AuthenticateAsync(UsernameBox.Text, PinBox.Password);
            if (user is null)
            {
                ErrorText.Text = "Usuario o PIN incorrecto.";
                PinBox.Clear();
                PinBox.Focus();
                return;
            }

            Session.CurrentUser = user;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
