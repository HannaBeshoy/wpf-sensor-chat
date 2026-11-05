using System.Net;
using System.Windows;
using System.Windows.Threading;
using Chat.Protocol;

namespace Chat.Server.Wpf;

public partial class MainWindow : Window
{
    private ChatServer? _server;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UserList.ItemsSource = _server?.OnlineUsers.ToList();
    }

    private void AddLog(string line)
    {
        // the server raises Log from its network tasks; the list belongs to the UI thread
        Dispatcher.InvokeAsync(() =>
        {
            LogList.Items.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            LogList.ScrollIntoView(LogList.Items[^1]);
        });
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Port must be 1 to 65535.");
            return;
        }
        try
        {
            _server = new ChatServer(new UserStore(UsersBox.Text), IPAddress.Any, port);
            _server.Log += AddLog;
            _server.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start: " + ex.Message);
            _server = null;
            return;
        }
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = $"Listening on port {port}. Clients register and log in from the client window.";
        _timer.Start();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_server != null) await _server.DisposeAsync();
        _server = null;
        UserList.ItemsSource = null;
        AddLog("stopped");
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "Stopped";
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_server != null) await _server.DisposeAsync();
    }
}
