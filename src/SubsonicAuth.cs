using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;

namespace StreamFin;

public sealed class SubsonicPassword
{
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed record SubsonicUser(Guid Id, string Name);

// Jellyfin keeps no plaintext passwords, and Subsonic token auth needs one (t = md5(password + salt)),
// so each user gets a separate app password from the plugin settings. Regenerating it revokes the old one.
public sealed class SubsonicAuth(IUserManager users)
{
    public const int FailureLimit = 10;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (int Count, DateTime Since)> failures = new();

    public static bool Enabled => Plugin.Instance?.Configuration is { Enabled: true, SubsonicEnabled: true } && MusicChannel.IsAvailable;

    private static IEnumerable<(Guid User, string Password)> Passwords() => (Plugin.Instance?.Configuration.SubsonicPasswords ?? [])
        .Where(p => p is not null && p.Password.Length >= 16 && Guid.TryParse(p.UserId, out _)).Select(p => (Guid.Parse(p.UserId), p.Password));

    private static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // u + t + s (token), u + p (plain or "enc:" hex), or the OpenSubsonic apiKey on its own.
    public static bool Verify(string password, string? token, string? salt, string? plain)
    {
        if (token is not null)
        {
            if (salt is not { Length: >= 6 and <= 128 }) return false;
            return Same(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + salt))), token.ToLowerInvariant());
        }
        if (plain is null) return false;
        if (plain.StartsWith("enc:", StringComparison.Ordinal))
        {
            try { plain = Encoding.UTF8.GetString(Convert.FromHexString(plain[4..])); }
            catch (FormatException) { return false; }
        }
        return Same(password, plain);
    }

    // Returns the user, or a Subsonic error code and message.
    public (SubsonicUser? User, int Code, string Message) Check(IPAddress? address, string? name, string? token, string? salt, string? plain, string? apiKey)
    {
        // Per address and name: behind a reverse proxy every client can share one address, and a stranger's
        // wrong guesses must not lock everyone else out. The app passwords are random, so this only limits noise.
        var key = (address?.ToString() ?? "") + "/" + (apiKey is not null ? "" : name?.ToLowerInvariant() ?? "");
        var now = DateTime.UtcNow;
        if (failures.TryGetValue(key, out var seen) && seen.Count >= FailureLimit && now - seen.Since < FailureWindow)
            return (null, 40, "Too many failed sign-ins. Try again in a few minutes.");
        SubsonicUser? Fail(out int code, int value)
        {
            code = value;
            failures.AddOrUpdate(key, (1, now), (_, old) => now - old.Since >= FailureWindow ? (1, now) : (old.Count + 1, old.Since));
            return null;
        }
        if (apiKey is not null)
        {
            if (name is not null || token is not null || plain is not null) return (null, 43, "Use either an API key or a username, not both.");
            var match = Guid.Empty;
            // Every entry is compared so the time taken does not depend on which one matches.
            foreach (var (user, password) in Passwords()) if (Same(password, apiKey)) match = user;
            if (match == Guid.Empty) return (Fail(out var code, 44), code, "Invalid API key.");
            return Allowed(match) is { } allowed ? (allowed, 0, "") : (Fail(out var refused, 44), refused, "Invalid API key.");
        }
        if (name is null || (token is null && plain is null)) return (null, 10, "Required parameter is missing: u and t/s or p, or apiKey.");
        var account = users.GetUserByName(name);
        var secret = account is null ? null : Passwords().Where(p => p.User == account.Id).Select(p => p.Password).FirstOrDefault();
        // An unknown name still costs a comparison, so it answers in the same time as a wrong password.
        var ok = Verify(secret ?? "streamfin-no-such-user-password", token, salt, plain) && secret is not null;
        if (!ok || Allowed(account!.Id) is not { } found) return (Fail(out var wrong, 40), wrong, "Wrong username or password.");
        failures.TryRemove(key, out _);
        return (found, 0, "");
    }

    private SubsonicUser? Allowed(Guid id) => Plugin.Allows(id) && users.GetUserById(id) is { } user && !user.HasPermission(PermissionKind.IsDisabled)
        ? new SubsonicUser(id, user.Username) : null;
}
