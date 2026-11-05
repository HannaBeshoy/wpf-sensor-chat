using System.Net.Sockets;
using System.Security.Cryptography;
using Chat.Protocol;

namespace Chat.Client;

/// <summary>
/// Client side of the chat, used by the WPF window and by the tests. After
/// ConnectAsync a background task reads frames and raises MessageReceived; file
/// chunks are assembled by FileReceiver and reported through FileReceived once the
/// SHA-256 of the whole file matches.
/// </summary>
public sealed class ChatClient : IAsyncDisposable
{
    private TcpClient? _tcp;
    private Stream? _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<Message>? _loginReply;
    private Task? _readLoop;
    public FileReceiver Files { get; } = new();

    public string? User { get; private set; }
    public event Action<Message>? MessageReceived;
    public event Action<ReceivedFile>? FileReceived;
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(string host, int port)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        _readLoop = ReadLoopAsync(_cts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        string reason = "server closed the connection";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Message? m = await FrameCodec.ReadAsync(_stream!, ct).ConfigureAwait(false);
                if (m == null) break;
                if (m.Type == MessageType.LoginResult) { _loginReply?.TrySetResult(m); continue; }
                if (m.Type == MessageType.FileChunk)
                {
                    var done = Files.Add(m);
                    if (done != null) FileReceived?.Invoke(done);
                    continue;
                }
                MessageReceived?.Invoke(m);
            }
        }
        catch (OperationCanceledException) { reason = "disconnected"; }
        catch (Exception e) when (e is IOException or ProtocolException or ObjectDisposedException) { reason = e.Message; }
        _loginReply?.TrySetException(new IOException(reason));
        Disconnected?.Invoke(reason);
    }

    public async Task SendAsync(Message m)
    {
        if (_stream == null) throw new InvalidOperationException("not connected");
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { await FrameCodec.WriteAsync(_stream, m, _cts.Token).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private async Task<Message> AskAsync(Message m)
    {
        _loginReply = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync(m).ConfigureAwait(false);
        return await _loginReply.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    public Task<Message> RegisterAsync(string user, string password) =>
        AskAsync(new Message { Type = MessageType.Register, User = user, Password = password });

    /// <returns>(ok, text) from the server.</returns>
    public async Task<(bool ok, string text)> LoginAsync(string user, string password)
    {
        var r = await AskAsync(new Message { Type = MessageType.Login, User = user, Password = password }).ConfigureAwait(false);
        if (r.Ok == true) User = r.User;
        return (r.Ok == true, r.Text ?? "");
    }

    public Task SendChatAsync(string text) => SendAsync(Message.ChatText(text));
    public Task SendPrivateAsync(string to, string text) => SendAsync(Message.PrivateText(to, text));

    /// <summary>Sends a file in 48 KiB chunks (64 KiB once in base64); the last chunk carries its SHA-256.</summary>
    public async Task SendFileAsync(string to, string fileName, byte[] content, IProgress<double>? progress = null)
    {
        const int chunk = 48 * 1024;
        string id = Guid.NewGuid().ToString("N");
        string sha = Convert.ToHexString(SHA256.HashData(content));
        for (long offset = 0; offset < content.Length || offset == 0; offset += chunk)
        {
            int n = (int)Math.Min(chunk, content.Length - offset);
            bool last = offset + n >= content.Length;
            await SendAsync(new Message
            {
                Type = MessageType.FileChunk, To = to, FileId = id, FileName = fileName, FileSize = content.Length,
                Offset = offset, Data = Convert.ToBase64String(content, (int)offset, n), Sha256 = last ? sha : null,
            }).ConfigureAwait(false);
            progress?.Report(content.Length == 0 ? 1 : (double)(offset + n) / content.Length);
            if (last) break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_stream != null) await SendAsync(new Message { Type = MessageType.Logout }).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
        _cts.Cancel();
        _tcp?.Dispose();
        if (_readLoop != null) await _readLoop.ConfigureAwait(false);
        _cts.Dispose();
    }
}

public sealed record ReceivedFile(string From, string FileName, byte[] Content, bool HashOk);

/// <summary>Collects FileChunk messages per file and checks the SHA-256 at the end.</summary>
public sealed class FileReceiver
{
    private readonly Dictionary<string, MemoryStream> _parts = new();

    public ReceivedFile? Add(Message m)
    {
        if (m.FileId == null || m.Data == null) return null;
        if (!_parts.TryGetValue(m.FileId, out var ms)) _parts[m.FileId] = ms = new MemoryStream();
        if (m.Offset != ms.Length) { _parts.Remove(m.FileId); return null; }          // out of order: drop the file
        byte[] data = Convert.FromBase64String(m.Data);
        ms.Write(data);
        if (ms.Length > (m.FileSize ?? 0)) { _parts.Remove(m.FileId); return null; }   // more than announced
        if (m.Sha256 == null) return null;
        _parts.Remove(m.FileId);
        byte[] content = ms.ToArray();
        bool ok = content.Length == m.FileSize && Convert.ToHexString(SHA256.HashData(content)) == m.Sha256;
        return new ReceivedFile(m.From ?? "?", Names.SafeFileName(m.FileName), content, ok);
    }
}
