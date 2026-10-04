using System.Windows;
using SupermercadoPOS.Domain;
using SupermercadoPOS.Services;

namespace SupermercadoPOS.Dialogs;

public partial class SupervisorApprovalDialog : Window
{
    public PosUser? ApprovedUser { get; private set; }

    public SupervisorApprovalDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    private async void Authorize_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
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
            ApprovedUser = user;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
