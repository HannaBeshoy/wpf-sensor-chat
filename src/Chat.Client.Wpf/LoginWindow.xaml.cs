using System.Windows;

namespace Chat.Client.Wpf;

public partial class LoginWindow : Window
{
    private ChatClient? _client;

    public LoginWindow() => InitializeComponent();

    private async Task<ChatClient?> ConnectAsync()
    {
        if (_client != null) return _client;
        if (!int.TryParse(PortBox.Text, out int port)) { StatusText.Text = "Port must be a number."; return null; }
        var c = new ChatClient();
        try { await c.ConnectAsync(HostBox.Text.Trim(), port); }
        catch (Exception ex) { StatusText.Text = "Cannot reach the server: " + ex.Message; return null; }
        c.Disconnected += reason => Dispatcher.InvokeAsync(() => { if (IsVisible) { StatusText.Text = "Disconnected: " + reason; _client = null; } });
        return _client = c;
    }

    private void Busy(bool busy) { LoginButton.IsEnabled = RegisterButton.IsEnabled = !busy; }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        Busy(true);
        try
        {
            var c = await ConnectAsync();
            if (c == null) return;
            var r = await c.RegisterAsync(UserBox.Text.Trim(), PasswordBox.Password);
            StatusText.Foreground = r.Ok == true ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.DarkRed;
            StatusText.Text = r.Text;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { Busy(false); }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        Busy(true);
        try
        {
            var c = await ConnectAsync();
            if (c == null) return;
            var (ok, text) = await c.LoginAsync(UserBox.Text.Trim(), PasswordBox.Password);
            if (!ok) { StatusText.Foreground = System.Windows.Media.Brushes.DarkRed; StatusText.Text = text; return; }
            new MainWindow(c).Show();
            Close();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { Busy(false); }
    }
}
