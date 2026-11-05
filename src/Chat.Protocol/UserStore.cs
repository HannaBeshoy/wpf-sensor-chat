using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chat.Protocol;

/// <summary>
/// User accounts for the server (grade 3: login with a user name and password).
/// Passwords are never stored: only a random 16-byte salt and a PBKDF2-SHA256 hash
/// (100,000 iterations), compared in constant time. Saved as JSON when a path is given.
/// </summary>
public sealed partial class UserStore
{
    public const int Iterations = 100_000;
    private readonly Dictionary<string, Account> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;
    private readonly object _lock = new();

    public sealed record Account(string Name, string Salt, string Hash, int Iterations);

    public UserStore(string? path = null)
    {
        _path = path;
        if (path != null && File.Exists(path) && new FileInfo(path).Length > 0)
            foreach (var a in JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(path)) ?? [])
                _users[a.Name] = a;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{3,20}$")]
    private static partial Regex NameRule();

    public static string? CheckName(string? name) =>
        name != null && NameRule().IsMatch(name) ? null : "user name: 3 to 20 letters, digits, - or _";

    public static string? CheckPassword(string? pw) =>
        pw != null && pw.Length >= 6 && pw.Length <= 128 ? null : "password: 6 to 128 characters";

    /// <returns>null on success, otherwise the reason.</returns>
    public string? Register(string? name, string? password)
    {
        string? err = CheckName(name) ?? CheckPassword(password);
        if (err != null) return err;
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password!, salt, Iterations, HashAlgorithmName.SHA256, 32);
        lock (_lock)
        {
            if (_users.ContainsKey(name!)) return "this user name is taken";
            _users[name!] = new Account(name!, Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations);
            if (_path != null) File.WriteAllText(_path, JsonSerializer.Serialize(_users.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        return null;
    }

    /// <returns>The account's exact name if the password is right, otherwise null.</returns>
    public string? Verify(string? name, string? password)
    {
        if (name == null || password == null) return null;
        Account? a;
        lock (_lock) _users.TryGetValue(name, out a);
        if (a == null)
        {
            // spend the same time as a real check, so timing does not reveal which names exist
            Rfc2898DeriveBytes.Pbkdf2(password, new byte[16], Iterations, HashAlgorithmName.SHA256, 32);
            return null;
        }
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(a.Salt), a.Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(a.Hash)) ? a.Name : null;
    }

    public int Count { get { lock (_lock) return _users.Count; } }
}
