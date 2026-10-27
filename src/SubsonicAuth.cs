using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public sealed class SubsonicPassword
{
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed record SubsonicUser(Guid Id, string Name);

// Jellyfin keeps no plaintext passwords, and Subsonic token auth needs one (t = md5(password + salt)),
// so each user gets a separate app password from the plugin settings. Regenerating it revokes the old one.
public sealed class SubsonicAuth(IUserManager users) : IDisposable
{
    public const int FailureLimit = 10;
    public const int NameLimit = 256;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(2);
    private sealed class Counter { public int Count; }
    // Bounded and expiring, so a stream of junk sign-ins cannot grow it without limit.
    private readonly MemoryCache failures = new(new MemoryCacheOptions { SizeLimit = 10_000 });

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

    // Returns the user, or a Subsonic error code and message. A right password always works: locking an account
    // after wrong guesses would let anyone lock out its owner, and the app passwords are random enough that
    // guessing is hopeless anyway. Repeated failures from one address only get slower answers.
    public async Task<(SubsonicUser? User, int Code, string Message)> CheckAsync(IPAddress? address, string? name, string? token, string? salt, string? plain, string? apiKey, CancellationToken ct)
    {
        var (user, code, message) = Check(name, token, salt, plain, apiKey);
        if (user is not null || code is not (40 or 44)) return (user, code, message);
        var key = address?.ToString() ?? "";
        var counter = failures.GetOrCreate(key, entry =>
        {
            entry.Size = 1;
            entry.AbsoluteExpirationRelativeToNow = FailureWindow;
            return new Counter();
        })!;
        if (Interlocked.Increment(ref counter.Count) > FailureLimit) await Task.Delay(FailureDelay, ct);
        return (user, code, message);
    }

    private (SubsonicUser? User, int Code, string Message) Check(string? name, string? token, string? salt, string? plain, string? apiKey)
    {
        if (apiKey is not null)
        {
            if (name is not null || token is not null || plain is not null) return (null, 43, "Use either an API key or a username, not both.");
            var match = Guid.Empty;
            // Every entry is compared so the time taken does not depend on which one matches.
            foreach (var (user, password) in Passwords()) if (Same(password, apiKey)) match = user;
            return match != Guid.Empty && Allowed(match) is { } allowed ? (allowed, 0, "") : (null, 44, "Invalid API key.");
        }
        if (name is null || (token is null && plain is null)) return (null, 10, "Required parameter is missing: u and t/s or p, or apiKey.");
        if (name.Length > NameLimit) return (null, 40, "Wrong username or password.");
        var account = users.GetUserByName(name);
        var secret = account is null ? null : Passwords().Where(p => p.User == account.Id).Select(p => p.Password).FirstOrDefault();
        // An unknown name still costs a comparison, so it answers in the same time as a wrong password.
        var ok = Verify(secret ?? "streamfin-no-such-user-password", token, salt, plain) && secret is not null;
        return ok && Allowed(account!.Id) is { } found ? (found, 0, "") : (null, 40, "Wrong username or password.");
    }

    private SubsonicUser? Allowed(Guid id) => Plugin.Allows(id) && users.GetUserById(id) is { } user && !user.HasPermission(PermissionKind.IsDisabled)
        ? new SubsonicUser(id, user.Username) : null;

    public void Dispose() => failures.Dispose();
}
