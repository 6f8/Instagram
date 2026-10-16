using System.IO;
using System.Text.Json;
using TL;

namespace TelegramContacts.Services;

/// <summary>One Telegram contact as the UI shows it.</summary>
public sealed record ContactInfo(long Id, string FirstName, string LastName, string Phone, string Username)
{
    public string DisplayName => $"{FirstName} {LastName}".Trim();
}

/// <summary>What the login needs next from the user.</summary>
public enum LoginStep { Code, Password, Name, Done }

/// <summary>Saved api_id / api_hash / phone (never the code or password).</summary>
public sealed class AppSettings
{
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = "";
    public string Phone { get; set; } = "";
}

/// <summary>Wraps WTelegramClient: login and contacts (list, add, edit, delete).</summary>
public sealed class TelegramService : IAsyncDisposable
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TelegramContacts");
    private static readonly string SessionPath = Path.Combine(DataDir, "session.dat");
    private static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");

    private WTelegram.Client? _client;
    private readonly Dictionary<long, User> _users = [];

    public User? Me => _client?.User;

    public static AppSettings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static void SaveSettings(AppSettings s)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
    }

    public static bool HasSession => File.Exists(SessionPath);

    /// <summary>Starts (or continues) the login. Pass the phone first, then the value the returned step asks for.</summary>
    public async Task<LoginStep> LoginAsync(AppSettings s, string value)
    {
        if (_client == null)
        {
            Directory.CreateDirectory(DataDir);
            _client = new WTelegram.Client(s.ApiId, s.ApiHash, SessionPath);
        }
        var next = await _client.Login(value);
        return next switch
        {
            null => LoginStep.Done,
            "verification_code" => LoginStep.Code,
            "password" => LoginStep.Password,
            "name" => LoginStep.Name,
            _ => throw new InvalidOperationException("خطوة دخول غير معروفة: " + next),
        };
    }

    public async Task<List<ContactInfo>> GetContactsAsync()
    {
        var client = Require();
        var result = await client.Contacts_GetContacts();
        if (result is not Contacts_Contacts contacts) return [.. _users.Values.Select(ToInfo)];
        _users.Clear();
        foreach (var c in contacts.contacts)
            if (contacts.users.TryGetValue(c.user_id, out var u)) _users[u.id] = u;
        return [.. _users.Values.Select(ToInfo).OrderBy(c => c.DisplayName, StringComparer.CurrentCulture)];
    }

    /// <summary>Adds a contact by phone number. Returns null when that number has no Telegram account.</summary>
    public async Task<ContactInfo?> AddAsync(string firstName, string lastName, string phone)
    {
        var client = Require();
        var imported = await client.Contacts_ImportContacts(
        [
            new InputPhoneContact
            {
                client_id = Random.Shared.NextInt64(1, long.MaxValue),
                phone = phone,
                first_name = firstName,
                last_name = lastName,
            },
        ]);
        if (imported.imported.Length == 0) return null;
        var userId = imported.imported[0].user_id;
        if (!imported.users.TryGetValue(userId, out var user)) return null;
        _users[user.id] = user;
        return ToInfo(user);
    }

    /// <summary>Renames an existing contact (Telegram stores the name per contact).</summary>
    public async Task<ContactInfo> EditAsync(long id, string firstName, string lastName)
    {
        var client = Require();
        var user = FindUser(id);
        var updates = await client.Contacts_AddContact(user, firstName, lastName, user.phone ?? "");
        // The updates carry the user with its new contact name.
        if (updates.Users.TryGetValue(id, out var fresh)) user = fresh;
        else { user.first_name = firstName; user.last_name = lastName; }
        _users[id] = user;
        return ToInfo(user);
    }

    public async Task DeleteAsync(IEnumerable<long> ids)
    {
        var client = Require();
        var targets = ids.Select(FindUser).Select(u => (InputUserBase)(InputUser)u).ToArray();
        if (targets.Length == 0) return;
        await client.Contacts_DeleteContacts(targets);
        foreach (var t in targets)
            if (t is InputUser iu) _users.Remove(iu.user_id);
    }

    public async Task LogoutAsync()
    {
        if (_client != null)
        {
            try { await _client.Auth_LogOut(); } catch { /* the session is deleted below anyway */ }
            await DisposeAsync();
        }
        try { File.Delete(SessionPath); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null) await _client.DisposeAsync();
        _client = null;
        _users.Clear();
    }

    private WTelegram.Client Require() =>
        _client?.User != null ? _client : throw new InvalidOperationException("سجّل الدخول أولاً.");

    private User FindUser(long id) =>
        _users.TryGetValue(id, out var u) ? u : throw new InvalidOperationException("جهة الاتصال غير موجودة، حدّث القائمة.");

    private static ContactInfo ToInfo(User u) =>
        new(u.id, u.first_name ?? "", u.last_name ?? "", u.phone ?? "", u.MainUsername ?? "");
}
