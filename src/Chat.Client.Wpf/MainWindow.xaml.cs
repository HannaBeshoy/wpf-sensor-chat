using System.IO;
using System.Windows;
using Chat.Protocol;
using Microsoft.Win32;

namespace Chat.Client.Wpf;

public partial class MainWindow : Window
{
    private readonly ChatClient _client;
    private readonly string _downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SensorChat");

    public MainWindow(ChatClient client)
    {
        InitializeComponent();
        _client = client;
        Title = $"Chat: {client.User}";
        // the client raises its events from the network task: hand them to the UI thread
        _client.MessageReceived += m => Dispatcher.InvokeAsync(() => Show(m));
        _client.FileReceived += f => Dispatcher.InvokeAsync(() => Save(f));
        _client.Disconnected += reason => Dispatcher.InvokeAsync(() => Add($"*** disconnected: {reason}"));
    }

    private void Add(string line)
    {
        Messages.Items.Add(line);
        Messages.ScrollIntoView(Messages.Items[^1]);
    }

    private void Show(Message m)
    {
        string t = (m.Time ?? DateTimeOffset.Now).ToString("HH:mm");
        switch (m.Type)
        {
            case MessageType.Chat: Add($"{t}  {m.From}: {m.Text}"); break;
            case MessageType.Private: Add($"{t}  {m.From} -> {m.To} (private): {m.Text}"); break;
            case MessageType.System: Add($"{t}  * {m.Text}"); break;
            case MessageType.Error: Add($"{t}  ! {m.Text}"); break;
            case MessageType.UserList: Users.ItemsSource = m.Users?.Where(u => u != _client.User).ToList(); break;
            case MessageType.Sensor:
                SensorText.Text = $"{m.From} at {t}: {m.Temp:0.0} °C, {(m.Dist is double d ? $"{d:0.0} cm" : "no echo")}, light {m.Light:0} %";
                break;
        }
    }

    private void Save(ReceivedFile f)
    {
        if (!f.HashOk) { Add($"! file {f.FileName} from {f.From} was damaged in transfer (SHA-256 mismatch) and was not saved"); return; }
        Directory.CreateDirectory(_downloads);
        string path = Path.Combine(_downloads, f.FileName);
        for (int k = 1; File.Exists(path); k++)
            path = Path.Combine(_downloads, $"{Path.GetFileNameWithoutExtension(f.FileName)} ({k}){Path.GetExtension(f.FileName)}");
        File.WriteAllBytes(path, f.Content);
        Add($"* file {f.FileName} ({f.Content.Length:N0} bytes) from {f.From} saved to {path}");
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        string text = InputBox.Text.Trim();
        if (text.Length == 0) return;
        try
        {
            if (PrivateBox.IsChecked == true)
            {
                if (Users.SelectedItem is not string to) { Add("! select a user on the right for a private message"); return; }
                await _client.SendPrivateAsync(to, text);
            }
            else await _client.SendChatAsync(text);
            InputBox.Clear();
        }
        catch (Exception ex) { Add("! " + ex.Message); }
    }

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        if (Users.SelectedItem is not string to) { Add("! select the user on the right who should receive the file"); return; }
        var dlg = new OpenFileDialog { Title = $"Send a file to {to}" };
        if (dlg.ShowDialog(this) != true) return;
        var info = new FileInfo(dlg.FileName);
        if (info.Length > 10 * 1024 * 1024) { Add("! files up to 10 MiB"); return; }
        FileProgress.Visibility = Visibility.Visible;
        try
        {
            await _client.SendFileAsync(to, info.Name, await File.ReadAllBytesAsync(info.FullName), new Progress<double>(p => FileProgress.Value = p));
            Add($"* sent {info.Name} ({info.Length:N0} bytes) to {to}");
        }
        catch (Exception ex) { Add("! " + ex.Message); }
        finally { FileProgress.Visibility = Visibility.Collapsed; }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => await _client.DisposeAsync();
}
