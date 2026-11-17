using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace StreamFin;

public sealed record SpotifyTrack(string Title, IReadOnlyList<string> Artists, string Album, int? DurationMs, string? Isrc);
public sealed record SpotifyPage(IReadOnlyList<SpotifyTrack> Tracks, bool HasNext);

// Imports a public Spotify playlist into a user's StreamFin playlists. Spotify's Web API needs a free developer app
// (client ID and secret in the settings); only the playlist is read, never anyone's account. Each song is matched by
// its ISRC first, then by title, artist and length, and plays as FLAC like any other.
public sealed class SpotifyImport(SafeHttp http, MusicCatalog catalog, MusicLibraryStore store, ILogger<SpotifyImport> logger)
{
    private static readonly Uri Accounts = new("https://accounts.spotify.com/api/token");
    private static readonly Uri Api = new("https://api.spotify.com/v1/");
    private static readonly Regex Link = new(@"(?:open\.spotify\.com/(?:intl-[a-z-]+/)?playlist/|spotify:playlist:)([A-Za-z0-9]{22})\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly ConcurrentDictionary<string, DateTime> Running = new();
    private (string Token, DateTime Until)? token;

    public static bool Configured => Plugin.Instance?.Configuration is { SpotifyClientId.Length: > 0, SpotifyClientSecret.Length: > 0 };
    public static string? PlaylistId(string? text) => text is null ? null : Link.Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    public static (string Name, string Description) ParsePlaylist(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string Text(string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        var name = Text("name");
        return (name.Length > 0 ? name[..Math.Min(name.Length, 200)] : "Spotify playlist", Regex.Replace(Text("description"), "<[^>]*>", "", RegexOptions.None, TimeSpan.FromSeconds(1)));
    }

    // One page of a playlist's items. Episodes, local files and removed songs carry no usable track and are skipped.
    public static SpotifyPage ParseTracks(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tracks = new List<SpotifyTrack>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("track", out var t) || t.ValueKind != JsonValueKind.Object) continue;
                string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (Text(t, "name") is not { Length: > 0 } title) continue;
                var artists = t.TryGetProperty("artists", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(a => Text(a, "name")).OfType<string>().Where(a => a.Length > 0).ToArray() : [];
                if (artists.Length == 0) continue;
                var album = t.TryGetProperty("album", out var a) ? Text(a, "name") ?? "" : "";
                int? ms = t.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) && n > 0 ? n : null;
                var isrc = t.TryGetProperty("external_ids", out var ids) ? Text(ids, "isrc")?.ToUpperInvariant() : null;
                tracks.Add(new SpotifyTrack(title, artists, album, ms, isrc is { Length: 12 } ? isrc : null));
            }
        var next = root.TryGetProperty("next", out var link) && link.ValueKind == JsonValueKind.String;
        return new SpotifyPage(tracks, next);
    }

    private async Task<IReadOnlyDictionary<string, string>> AuthAsync(CancellationToken ct)
    {
        if (token is { } known && known.Until > DateTime.UtcNow) return new Dictionary<string, string> { ["Authorization"] = "Bearer " + known.Token };
        var config = Plugin.Instance?.Configuration ?? throw new ProviderException("StreamFin is not loaded.");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.SpotifyClientId.Trim() + ":" + config.SpotifyClientSecret.Trim()));
        string json;
        try
        {
            json = await http.TextAsync(Accounts, ct, headers: new Dictionary<string, string> { ["Authorization"] = "Basic " + basic },
                open: true, form: new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
        }
        catch (SourceStatusException status) when (status.Status is 400 or 401) { throw new ProviderException("Spotify did not accept the client ID and secret in the StreamFin settings."); }
        using var document = JsonDocument.Parse(json);
        var access = document.RootElement.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : throw new ProviderException("Spotify sent no access token.");
        var seconds = document.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        token = (access, DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60)));
        return new Dictionary<string, string> { ["Authorization"] = "Bearer " + access };
    }

    // Starts an import in the background; the playlist appears at once and fills in as songs are matched.
    // A second paste of the same link while it runs does nothing.
    public bool Start(Guid user, string link)
    {
        if (!Configured || PlaylistId(link) is not { } id) return false;
        var key = user.ToString("N") + "/" + id;
        if (Running.TryGetValue(key, out var since) && DateTime.UtcNow - since < TimeSpan.FromMinutes(30)) return true;
        Running[key] = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            try { await ImportAsync(user, id, limit.Token); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or IOException or TaskCanceledException)
            {
                logger.LogWarning("StreamFin could not import a Spotify playlist ({ErrorType})", ex.GetType().Name);
            }
            finally { Running.TryRemove(key, out _); }
        });
        return true;
    }

    public async Task ImportAsync(Guid user, string playlistId, CancellationToken ct)
    {
        var auth = await AuthAsync(ct);
        string meta;
        try { meta = await http.TextAsync(new Uri(Api, $"playlists/{playlistId}?fields=name,description"), ct, headers: auth, open: true); }
        catch (SourceStatusException status) when (status.Status == 404) { throw new ProviderException("Spotify does not share this playlist (private, or a Spotify-made playlist)."); }
        var (name, description) = ParsePlaylist(meta);
        var created = DateTime.UtcNow;
        var playlist = new MusicPlaylist(Guid.NewGuid(), name, "Importing from Spotify…", created, created, []);
        await store.UpdateAsync(user, library => library.Playlists.Insert(0, playlist), ct);

        var entries = new List<MusicPlay>();
        var (seen, offset) = (0, 0);
        while (seen < MusicLibraryStore.PlaylistEntryLimit)
        {
            var page = ParseTracks(await http.TextAsync(new Uri(Api, $"playlists/{playlistId}/tracks?limit=100&offset={offset}"
                + "&fields=next,items(track(name,duration_ms,external_ids(isrc),artists(name),album(name)))"), ct, headers: auth, open: true));
            foreach (var song in page.Tracks)
            {
                if (++seen > MusicLibraryStore.PlaylistEntryLimit) break;
                if (await MatchAsync(song, ct) is { } track) entries.Add(new MusicPlay(track, song.Album, DateTime.UtcNow));
            }
            await SaveAsync(user, playlist.Id, entries, $"Importing from Spotify… {entries.Count} of {seen} songs found", ct);
            if (!page.HasNext) break;
            offset += 100;
        }
        var missed = seen - entries.Count;
        await SaveAsync(user, playlist.Id, entries, "Imported from Spotify" + (description.Length > 0 ? " · " + description[..Math.Min(description.Length, 200)] : "")
            + (missed > 0 ? $" · {missed} of {seen} songs were not found" : ""), ct);
    }

    private Task SaveAsync(Guid user, Guid id, List<MusicPlay> entries, string comment, CancellationToken ct) => store.UpdateAsync(user, library =>
    {
        var at = library.Playlists.FindIndex(p => p.Id == id);
        if (at >= 0) library.Playlists[at] = library.Playlists[at] with { Entries = entries.ToArray(), Comment = comment, Changed = DateTime.UtcNow };
    }, ct);

    private async Task<MusicTrack?> MatchAsync(SpotifyTrack song, CancellationToken ct)
    {
        try
        {
            if (song.Isrc is { } isrc && await catalog.ByIsrcAsync(isrc, ct) is { } exact) return exact;
            var results = await catalog.SearchAsync(song.Artists[0] + " " + song.Title, ct);
            return MusicCatalog.BestMatch(song.Title, song.Artists[0], song.DurationMs, results.Tracks);
        }
        // One song the catalogs cannot answer for is left out; the rest of the playlist still imports.
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { return null; }
    }
}
