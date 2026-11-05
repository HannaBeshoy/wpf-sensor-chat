using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Chat.Protocol;

namespace Chat.Server;

/// <summary>
/// The chat server: one TcpListener, one async read loop per client (no thread per
/// client: await frees the thread while a client is silent). Logged-in clients are kept
/// in a concurrent dictionary by user name; each has a send lock, because two tasks
/// writing frames to one stream at the same time would interleave their bytes.
/// </summary>
public sealed class ChatServer : IAsyncDisposable
{
    public const long MaxFileSize = 10 * 1024 * 1024;     // 10 MiB per file
    public const int MaxChunk = 64 * 1024;                // 64 KiB of data per FileChunk
    public const int MaxFailedLogins = 5;                 // per connection, against password guessing

    private readonly UserStore _users;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, Session> _online = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>Raised for every event worth showing in the server window (joins, leaves, refusals).</summary>
    public event Action<string>? Log;

    public ChatServer(UserStore users, IPAddress address, int port)
    {
        _users = users;
        _listener = new TcpListener(address, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public IReadOnlyCollection<string> OnlineUsers => _online.Keys.OrderBy(k => k).ToArray();

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        Log?.Invoke($"listening on port {Port}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private sealed class Session(TcpClient client)
    {
        public TcpClient Client { get; } = client;
        public Stream Stream { get; } = client.GetStream();
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public string? User { get; set; }
        public string Remote { get; } = client.Client.RemoteEndPoint?.ToString() ?? "?";
        public Dictionary<string, long> IncomingFiles { get; } = new();   // FileId -> bytes relayed so far
        public int FailedLogins { get; set; }

        public async Task SendAsync(Message m, CancellationToken ct)
        {
            await SendLock.WaitAsync(ct).ConfigureAwait(false);
            try { await FrameCodec.WriteAsync(Stream, m, ct).ConfigureAwait(false); }
            finally { SendLock.Release(); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var s = new Session(client);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Message? m = await FrameCodec.ReadAsync(s.Stream, ct).ConfigureAwait(false);
                if (m == null || m.Type == MessageType.Logout) break;
                if (s.User == null) { if (!await HandleLoginAsync(s, m, ct).ConfigureAwait(false)) break; }
                else await HandleMessageAsync(s, m, ct).ConfigureAwait(false);
            }
        }
        catch (ProtocolException e) { Log?.Invoke($"{s.User ?? s.Remote}: protocol error, disconnected ({e.Message})"); }
        catch (IOException) { }                   // the connection dropped
        catch (OperationCanceledException) { }
        finally
        {
            if (s.User != null && _online.TryRemove(s.User, out _))
            {
                Log?.Invoke($"{s.User} left");
                await BroadcastAsync(Message.SystemText($"{s.User} left the chat"), ct).ConfigureAwait(false);
                await BroadcastUserListAsync(ct).ConfigureAwait(false);
            }
            client.Dispose();
        }
    }

    /// <returns>false if the connection should be closed.</returns>
    private async Task<bool> HandleLoginAsync(Session s, Message m, CancellationToken ct)
    {
        switch (m.Type)
        {
            case MessageType.Register:
                string? err = _users.Register(m.User, m.Password);
                await s.SendAsync(new Message { Type = MessageType.LoginResult, Ok = err == null, Text = err ?? "account created, now log in" }, ct).ConfigureAwait(false);
                if (err == null) Log?.Invoke($"new account: {m.User}");
                return true;
            case MessageType.Login:
                string? name = _users.Verify(m.User, m.Password);
                if (name == null)
                {
                    Log?.Invoke($"failed login for '{m.User}' from {s.Remote}");
                    await s.SendAsync(new Message { Type = MessageType.LoginResult, Ok = false, Text = "wrong user name or password" }, ct).ConfigureAwait(false);
                    return ++s.FailedLogins < MaxFailedLogins;   // a few tries, then the connection is closed
                }
                if (!_online.TryAdd(name, s))
                {
                    await s.SendAsync(new Message { Type = MessageType.LoginResult, Ok = false, Text = $"{name} is already logged in" }, ct).ConfigureAwait(false);
                    return true;
                }
                s.User = name;
                await s.SendAsync(new Message { Type = MessageType.LoginResult, Ok = true, User = name, Text = "welcome" }, ct).ConfigureAwait(false);
                Log?.Invoke($"{name} joined from {s.Remote}");
                await BroadcastAsync(Message.SystemText($"{name} joined the chat"), ct).ConfigureAwait(false);
                await BroadcastUserListAsync(ct).ConfigureAwait(false);
                return true;
            default:
                await s.SendAsync(Message.ErrorText("log in first"), ct).ConfigureAwait(false);
                return false;
        }
    }

    private async Task HandleMessageAsync(Session s, Message m, CancellationToken ct)
    {
        // the server sets From and Time itself: a client cannot pretend to be someone else
        m.From = s.User;
        m.Time = DateTimeOffset.Now;
        m.Password = null;
        switch (m.Type)
        {
            case MessageType.Chat:
                if (string.IsNullOrWhiteSpace(m.Text) || m.Text.Length > 4000) { await s.SendAsync(Message.ErrorText("message must be 1 to 4000 characters"), ct).ConfigureAwait(false); return; }
                await BroadcastAsync(m, ct).ConfigureAwait(false);
                break;
            case MessageType.Private:
                if (string.IsNullOrWhiteSpace(m.Text) || m.Text.Length > 4000) { await s.SendAsync(Message.ErrorText("message must be 1 to 4000 characters"), ct).ConfigureAwait(false); return; }
                if (m.To == null || !_online.TryGetValue(m.To, out var target)) { await s.SendAsync(Message.ErrorText($"{m.To} is not online"), ct).ConfigureAwait(false); return; }
                await TrySendAsync(target, m, ct).ConfigureAwait(false);
                if (target != s) await s.SendAsync(m, ct).ConfigureAwait(false);   // the sender sees it in its own window too
                break;
            case MessageType.FileChunk:
                await RelayFileChunkAsync(s, m, ct).ConfigureAwait(false);
                break;
            case MessageType.Sensor:
                await BroadcastAsync(m, ct).ConfigureAwait(false);
                break;
            default:
                await s.SendAsync(Message.ErrorText($"unexpected {m.Type}"), ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task RelayFileChunkAsync(Session s, Message m, CancellationToken ct)
    {
        // the server relays file chunks to one user and checks sizes; it never stores the file
        if (m.To == null || !_online.TryGetValue(m.To, out var target)) { await s.SendAsync(Message.ErrorText($"{m.To} is not online"), ct).ConfigureAwait(false); return; }
        if (m.FileId == null || m.FileName == null || m.FileSize is not long size || m.Offset is not long offset || m.Data == null)
        { await s.SendAsync(Message.ErrorText("incomplete file chunk"), ct).ConfigureAwait(false); return; }
        int bytes = m.Data.Length / 4 * 3;     // approximate decoded size, enough for the limits
        if (size > MaxFileSize || bytes > MaxChunk + 3) { await s.SendAsync(Message.ErrorText($"files up to {MaxFileSize / 1024 / 1024} MiB, chunks up to 64 KiB"), ct).ConfigureAwait(false); return; }
        s.IncomingFiles.TryGetValue(m.FileId, out long sent);
        if (offset != sent) { await s.SendAsync(Message.ErrorText("file chunks out of order"), ct).ConfigureAwait(false); return; }
        s.IncomingFiles[m.FileId] = sent + bytes;
        if (m.Sha256 != null) s.IncomingFiles.Remove(m.FileId);   // last chunk
        m.FileName = Names.SafeFileName(m.FileName);              // never pass on a path
        await TrySendAsync(target, m, ct).ConfigureAwait(false);
    }

    private async Task BroadcastAsync(Message m, CancellationToken ct)
    {
        foreach (var s in _online.Values) await TrySendAsync(s, m, ct).ConfigureAwait(false);
    }

    private Task BroadcastUserListAsync(CancellationToken ct) =>
        BroadcastAsync(new Message { Type = MessageType.UserList, Users = OnlineUsers.ToArray() }, ct);

    private static async Task TrySendAsync(Session s, Message m, CancellationToken ct)
    {
        try { await s.SendAsync(m, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }   // that client is going away
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        foreach (var s in _online.Values) s.Client.Dispose();
        if (_acceptLoop != null) await _acceptLoop.ConfigureAwait(false);
        _cts.Dispose();
    }
}
