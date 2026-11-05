// Tests for the protocol, the user store, the server and the client.
// Each test is a method; Run() catches failures and prints PASS or FAIL.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Chat.Client;
using Chat.Protocol;
using Chat.Server;

int passed = 0, failed = 0;
async Task Run(string name, Func<Task> test)
{
    try { await test().WaitAsync(TimeSpan.FromSeconds(30)); Console.WriteLine($"PASS  {name}"); passed++; }
    catch (Exception e) { Console.WriteLine($"FAIL  {name}: {e.GetType().Name}: {e.Message}"); failed++; }
}
void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

// a stream that hands out at most one byte per read: TCP may split frames anywhere
var users = new UserStore();
users.Register("anna", "secret-a");
users.Register("bela", "secret-b");
users.Register("csaba", "secret-c");
users.Register("sensor-node", "esp32-pass");

await Run("frame round trip, also when read one byte at a time", async () =>
{
    var m = new Message { Type = MessageType.Chat, Text = "szia, ÁRVÍZTŰRŐ tükörfúrógép ✓" };
    var ms = new MemoryStream();
    await FrameCodec.WriteAsync(ms, m);
    await FrameCodec.WriteAsync(ms, Message.PrivateText("bela", "second"));
    ms.Position = 0;
    var slow = new OneByteStream(ms);
    var a = await FrameCodec.ReadAsync(slow);
    var b = await FrameCodec.ReadAsync(slow);
    Check(a!.Text == m.Text && b!.To == "bela" && await FrameCodec.ReadAsync(slow) == null, "messages differ");
});

await Run("frame layout: 4-byte big-endian length, camelCase JSON, nulls left out", () =>
{
    byte[] f = FrameCodec.Encode(Message.ChatText("hi"));
    string json = Encoding.UTF8.GetString(f, 4, f.Length - 4);
    Check(BinaryPrimitives.ReadInt32BigEndian(f) == f.Length - 4, "length prefix");
    Check(json == "{\"type\":\"Chat\",\"text\":\"hi\"}", json);
    return Task.CompletedTask;
});

await Run("oversized, negative and cut frames are refused", async () =>
{
    foreach (int len in new[] { FrameCodec.MaxFrame + 1, -5, 0 })
    {
        var ms = new MemoryStream(); var h = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(h, len); ms.Write(h); ms.Position = 0;
        try { await FrameCodec.ReadAsync(ms); throw new Exception($"accepted length {len}"); } catch (ProtocolException) { }
    }
    var cut = new MemoryStream(FrameCodec.Encode(Message.ChatText("hello"))[..8]);
    try { await FrameCodec.ReadAsync(cut); throw new Exception("accepted a cut frame"); } catch (ProtocolException) { }
    var bad = new MemoryStream(); var hb = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(hb, 5); bad.Write(hb); bad.Write("{nope"u8); bad.Position = 0;
    try { await FrameCodec.ReadAsync(bad); throw new Exception("accepted bad JSON"); } catch (ProtocolException) { }
});

await Run("user store: hashes, not passwords; rules; constant-time check", () =>
{
    string path = Path.GetTempFileName();
    var s = new UserStore(path);
    Check(s.Register("dora", "password1") == null, "register");
    Check(s.Register("DORA", "other123") != null, "duplicate (case-insensitive) accepted");
    Check(s.Register("x", "password1") != null && s.Register("ok_name", "123") != null && s.Register("bad name", "password1") != null, "rules");
    string file = File.ReadAllText(path);
    Check(!file.Contains("password1") && file.Contains("\"Salt\""), "password stored in clear");
    var again = new UserStore(path);
    Check(again.Verify("Dora", "password1") == "dora" && again.Verify("dora", "password2") == null && again.Verify("nobody", "x") == null, "verify");
    return Task.CompletedTask;
});

await using var server = new ChatServer(users, IPAddress.Loopback, 0);
var log = new ConcurrentQueue<string>();
server.Log += log.Enqueue;
server.Start();
int port = server.Port;

async Task<(ChatClient c, ConcurrentQueue<Message> inbox)> Connect(string user, string pw)
{
    // the server removes a closed session a moment after the client disposes it: wait for that
    for (int i = 0; i < 300 && server.OnlineUsers.Contains(user, StringComparer.OrdinalIgnoreCase); i++) await Task.Delay(10);
    var c = new ChatClient();
    var inbox = new ConcurrentQueue<Message>();
    c.MessageReceived += inbox.Enqueue;
    await c.ConnectAsync("127.0.0.1", port);
    var (ok, text) = await c.LoginAsync(user, pw);
    Check(ok, $"login {user}: {text}");
    return (c, inbox);
}
async Task<Message> WaitFor(ConcurrentQueue<Message> q, Func<Message, bool> match, int ms = 3000)
{
    var end = DateTime.Now.AddMilliseconds(ms);
    var skipped = new List<Message>();
    while (DateTime.Now < end)
    {
        while (q.TryDequeue(out var m)) { if (match(m)) { foreach (var s in skipped) q.Enqueue(s); return m; } skipped.Add(m); }
        await Task.Delay(10);
    }
    foreach (var s in skipped) q.Enqueue(s);
    throw new Exception("expected message did not arrive");
}

await Run("grade 2+3: login, broadcast to everyone, clean exit", async () =>
{
    var (a, ai) = await Connect("anna", "secret-a");
    var (b, bi) = await Connect("bela", "secret-b");
    await WaitFor(ai, m => m.Type == MessageType.UserList && m.Users!.Length == 2);
    await a.SendChatAsync("hello everyone");
    var got = await WaitFor(bi, m => m.Type == MessageType.Chat);
    Check(got.From == "anna" && got.Text == "hello everyone" && got.Time != null, "broadcast");
    await WaitFor(ai, m => m.Type == MessageType.Chat);                        // the sender sees it too
    await b.DisposeAsync();
    await WaitFor(ai, m => m.Type == MessageType.System && m.Text!.Contains("bela left"));
    var list = await WaitFor(ai, m => m.Type == MessageType.UserList && m.Users!.Length == 1);
    await a.DisposeAsync();
});

await Run("wrong password refused; 5 wrong tries close the connection; double login refused", async () =>
{
    var c = new ChatClient();
    bool closed = false;
    c.Disconnected += _ => closed = true;
    await c.ConnectAsync("127.0.0.1", port);
    for (int i = 0; i < 5; i++) Check(!(await c.LoginAsync("anna", "wrong")).ok, "accepted a wrong password");
    await Task.Delay(300);
    Check(closed, "connection still open after 5 failures");
    var (a, _) = await Connect("anna", "secret-a");
    var d = new ChatClient(); await d.ConnectAsync("127.0.0.1", port);
    var (ok, text) = await d.LoginAsync("anna", "secret-a");
    Check(!ok && text.Contains("already"), "second login of the same user accepted");
    await d.DisposeAsync(); await a.DisposeAsync();
});

await Run("grade 4: a private message reaches only its addressee", async () =>
{
    var (a, ai) = await Connect("anna", "secret-a");
    var (b, bi) = await Connect("bela", "secret-b");
    var (c, ci) = await Connect("csaba", "secret-c");
    await a.SendPrivateAsync("bela", "only for you");
    var got = await WaitFor(bi, m => m.Type == MessageType.Private);
    Check(got.From == "anna" && got.To == "bela" && got.Text == "only for you", "private");
    await WaitFor(ai, m => m.Type == MessageType.Private);                     // copy for the sender
    await Task.Delay(200);
    Check(!ci.Any(m => m.Type == MessageType.Private), "a third user saw the private message");
    await a.SendPrivateAsync("nobody", "x");
    await WaitFor(ai, m => m.Type == MessageType.Error && m.Text!.Contains("not online"));
    await a.DisposeAsync(); await b.DisposeAsync(); await c.DisposeAsync();
});

await Run("a client cannot pretend to be another user", async () =>
{
    var (a, _) = await Connect("anna", "secret-a");
    var (b, bi) = await Connect("bela", "secret-b");
    await a.SendAsync(new Message { Type = MessageType.Chat, From = "bela", Text = "I am bela" });
    var got = await WaitFor(bi, m => m.Type == MessageType.Chat);
    Check(got.From == "anna", "From was not overwritten");
    await a.DisposeAsync(); await b.DisposeAsync();
});

await Run("grade 5: a 1 MB file arrives intact (SHA-256) and only at its addressee", async () =>
{
    var (a, _) = await Connect("anna", "secret-a");
    var (b, _) = await Connect("bela", "secret-b");
    var (c, _) = await Connect("csaba", "secret-c");
    var got = new TaskCompletionSource<ReceivedFile>();
    bool cGot = false;
    b.FileReceived += f => got.TrySetResult(f);
    c.FileReceived += _ => cGot = true;
    byte[] content = RandomNumberGenerator.GetBytes(1_000_000);
    double last = 0;
    await a.SendFileAsync("bela", @"C:\Users\anna\secret\..\report.bin", content, new Progress<double>(p => last = p));
    var f = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(f.HashOk && f.Content.SequenceEqual(content) && f.From == "anna", "file differs");
    Check(f.FileName == "report.bin", $"path not stripped: {f.FileName}");
    await Task.Delay(200);
    Check(!cGot, "a third user received the file");
    await a.DisposeAsync(); await b.DisposeAsync(); await c.DisposeAsync();
});

await Run("an empty file and a file with a wrong hash", async () =>
{
    var r = new FileReceiver();
    var ok = r.Add(new Message { Type = MessageType.FileChunk, FileId = "1", FileName = "e.txt", FileSize = 0, Offset = 0, Data = "", Sha256 = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())) });
    Check(ok != null && ok.HashOk && ok.Content.Length == 0, "empty file");
    var bad = r.Add(new Message { Type = MessageType.FileChunk, FileId = "2", FileName = "b.txt", FileSize = 3, Offset = 0, Data = Convert.ToBase64String("abc"u8.ToArray()), Sha256 = "00" });
    Check(bad != null && !bad.HashOk, "wrong hash accepted");
    await Task.CompletedTask;
});

await Run("oversized file refused by the server", async () =>
{
    var (a, ai) = await Connect("anna", "secret-a");
    var (b, _) = await Connect("bela", "secret-b");
    await a.SendAsync(new Message { Type = MessageType.FileChunk, To = "bela", FileId = "x", FileName = "big.iso", FileSize = 50_000_000, Offset = 0, Data = "AAAA" });
    await WaitFor(ai, m => m.Type == MessageType.Error && m.Text!.Contains("MiB"));
    await a.DisposeAsync(); await b.DisposeAsync();
});

await Run("the ESP32's bytes: hand-written frames from a raw socket post a Sensor reading", async () =>
{
    var (a, ai) = await Connect("anna", "secret-a");
    using var raw = new TcpClient();
    await raw.ConnectAsync("127.0.0.1", port);
    var st = raw.GetStream();
    // exactly what firmware/esp32_chat_client builds with snprintf
    async Task SendRaw(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] h = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(h, body.Length);
        await st.WriteAsync(h); await st.WriteAsync(body);
    }
    await SendRaw("{\"type\":\"Login\",\"user\":\"sensor-node\",\"password\":\"esp32-pass\"}");
    var reply = await FrameCodec.ReadAsync(st);
    Check(reply!.Type == MessageType.LoginResult && reply.Ok == true, "ESP32 login");
    await SendRaw("{\"type\":\"Sensor\",\"temp\":23.4,\"dist\":41.7,\"light\":63}");
    var s = await WaitFor(ai, m => m.Type == MessageType.Sensor);
    Check(s.From == "sensor-node" && s.Temp == 23.4 && s.Dist == 41.7 && s.Light == 63, "sensor values");
    raw.Close();
    await WaitFor(ai, m => m.Type == MessageType.System && m.Text!.Contains("sensor-node left"));
    await a.DisposeAsync();
});

await Run("garbage on the socket disconnects only that client", async () =>
{
    var (a, ai) = await Connect("anna", "secret-a");
    using var raw = new TcpClient();
    await raw.ConnectAsync("127.0.0.1", port);
    await raw.GetStream().WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 1, 2, 3 });       // a 2 GB length
    await Task.Delay(300);
    Check(log.Any(l => l.Contains("protocol error")), "server did not log the protocol error");
    await a.SendChatAsync("still alive");
    await WaitFor(ai, m => m.Type == MessageType.Chat && m.Text == "still alive");
    await a.DisposeAsync();
});

await Run("300 messages from 3 clients at once: none lost, none garbled, each sender in order", async () =>
{
    var names = new[] { ("anna", "secret-a"), ("bela", "secret-b"), ("csaba", "secret-c") };
    var clients = new List<(ChatClient c, ConcurrentQueue<Message> q)>();
    foreach (var (u, p) in names) clients.Add(await Connect(u, p));
    await Task.Delay(200);
    foreach (var (_, q) in clients) q.Clear();
    await Task.WhenAll(clients.Select((cl, i) => Task.Run(async () =>
    {
        for (int k = 0; k < 100; k++) await cl.c.SendChatAsync($"{i}:{k}");
    })));
    var end = DateTime.Now.AddSeconds(10);
    while (DateTime.Now < end && clients.Any(cl => cl.q.Count(m => m.Type == MessageType.Chat) < 300)) await Task.Delay(50);
    foreach (var (_, q) in clients)
    {
        var chats = q.Where(m => m.Type == MessageType.Chat).Select(m => m.Text!).ToList();
        Check(chats.Count == 300 && chats.Distinct().Count() == 300, $"got {chats.Count}");
        for (int i = 0; i < 3; i++)                                     // each sender's messages stay in order
        {
            var mine = chats.Where(t => t.StartsWith($"{i}:")).Select(t => int.Parse(t[2..])).ToList();
            Check(mine.SequenceEqual(Enumerable.Range(0, 100)), "order");
        }
    }
    foreach (var (c, _) in clients) await c.DisposeAsync();
});

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class OneByteStream(Stream inner) : Stream
{
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(1, count));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
