using System.Windows;
using Abasto.Services;

namespace Abasto.Dialogs;

public partial class InitialSetupWindow : Window
{
    public InitialSetupWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => DisplayNameBox.Focus();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (PinBox.Password != ConfirmPinBox.Password)
        {
            ErrorText.Text = "Los PIN no coinciden.";
            ConfirmPinBox.Clear();
            ConfirmPinBox.Focus();
            return;
        }

        CreateButton.IsEnabled = false;
        try
        {
            await PosService.CreateInitialAdministratorAsync(UsernameBox.Text, DisplayNameBox.Text, PinBox.Password);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            PinBox.Clear();
            ConfirmPinBox.Clear();
            PinBox.Focus();
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }
}
