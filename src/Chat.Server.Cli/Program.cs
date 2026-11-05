// Headless chat server, for a Raspberry Pi, a Linux machine, or the ESP32 test without a Windows PC.
//   dotnet run --project src/Chat.Server.Cli -- [--port 5050] [--users users.json] [--add name password]
using System.Net;
using Chat.Protocol;
using Chat.Server;

int port = 5050;
string usersPath = "users.json";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port") port = int.Parse(args[++i]);
    else if (args[i] == "--users") usersPath = args[++i];
    else if (args[i] == "--add")
    {
        var err = new UserStore(usersPath).Register(args[i + 1], args[i + 2]);
        Console.WriteLine(err ?? $"added {args[i + 1]}");
        return err == null ? 0 : 1;
    }
}
await using var server = new ChatServer(new UserStore(usersPath), IPAddress.Any, port);
server.Log += line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
server.Start();
var quit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
await quit.Task;
return 0;
