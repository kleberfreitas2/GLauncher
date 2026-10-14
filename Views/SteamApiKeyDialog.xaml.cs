using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace GameLauncher.Views;

public partial class SteamApiKeyDialog : Window
{
    public string ApiKey => KeyBox.Password.Trim();

    public SteamApiKeyDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => KeyBox.Focus();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            MessageBox.Show("Informe uma chave API Steam válida.", "Chave API Steam",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
