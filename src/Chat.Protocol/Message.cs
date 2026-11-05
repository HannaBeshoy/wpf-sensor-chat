namespace Chat.Protocol;

/// <summary>Kinds of message on the wire. The JSON carries the name, e.g. "type":"Chat".</summary>
public enum MessageType
{
    Register,      // client -> server: create an account (User, Password)
    Login,         // client -> server: log in (User, Password)
    LoginResult,   // server -> client: Ok, Text (reason when refused)
    UserList,      // server -> clients: Users (who is online)
    Chat,          // client -> server -> everyone: Text
    Private,       // client -> server -> one user (To): Text
    FileChunk,     // client -> server -> one user (To): FileId, FileName, FileSize, Offset, Data (base64), Sha256 on the last chunk
    Sensor,        // sensor client -> server -> everyone: Temp, Dist, Light
    System,        // server -> clients: Text ("anna joined")
    Error,         // server -> client: Text
    Logout,        // client -> server
}

/// <summary>One message. Only the fields its type needs are set; null fields are not sent.</summary>
public sealed class Message
{
    public MessageType Type { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Text { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public bool? Ok { get; set; }
    public string[]? Users { get; set; }
    public string? FileId { get; set; }
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public long? Offset { get; set; }
    public string? Data { get; set; }
    public string? Sha256 { get; set; }
    public double? Temp { get; set; }
    public double? Dist { get; set; }
    public double? Light { get; set; }
    public DateTimeOffset? Time { get; set; }

    public static Message ChatText(string text) => new() { Type = MessageType.Chat, Text = text };
    public static Message PrivateText(string to, string text) => new() { Type = MessageType.Private, To = to, Text = text };
    public static Message SystemText(string text) => new() { Type = MessageType.System, Text = text, Time = DateTimeOffset.Now };
    public static Message ErrorText(string text) => new() { Type = MessageType.Error, Text = text };
}

public static class Names
{
    /// <summary>
    /// Only the last part of a file name, whatever separator the sender's system uses
    /// (a Windows client sends "C:\Users\..\x.bin", a Linux client "/home/../x.bin"), with
    /// characters Windows does not allow replaced: a received file can never be written
    /// outside the folder the user chose.
    /// </summary>
    public static string SafeFileName(string? name)
    {
        string n = (name ?? "").Replace('\\', '/');
        n = n[(n.LastIndexOf('/') + 1)..].Trim();
        foreach (char c in "<>:\"|?*") n = n.Replace(c, '_');
        n = new string(n.Where(c => !char.IsControl(c)).ToArray());
        return n is "" or "." or ".." ? "file" : n.Length > 120 ? n[..120] : n;
    }
}
