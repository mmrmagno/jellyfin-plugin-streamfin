using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamFin;

// One song as a playlist from elsewhere describes it. Only what is needed to find the same recording here.
// Video is a YouTube video's whole title and channel, kept for a last search when the title does not split cleanly.
public sealed record ImportTrack(string Title, string Artist, string? Album, int? DurationMs, string? Isrc, string? Video = null);

// A playlist read from somewhere: its name, its songs, and how to read it again (null when it cannot be, such as pasted
// text). Missing counts songs the source has but could not hand over (Spotify's embed stops at 100).
public sealed record ImportList(string Name, IReadOnlyList<ImportTrack> Tracks, string? Source, int Missing = 0, string? Note = null);

// What kind of input a paste is. Order matters: a single Spotify playlist link is a link, many track links are a list.
public enum ImportKind { None, SpotifyPlaylist, SpotifyAlbum, SpotifyTracks, YouTube, Deezer, Csv, Text }

public static class ImportReaders
{
    public const int TrackLimit = MusicLibraryStore.PlaylistEntryLimit;
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SpotifyLink = Rx(@"(?:open\.spotify\.com/(?:intl-[a-z-]+/)?(playlist|album)/|spotify:(playlist|album):)([A-Za-z0-9]{22})\b");
    private static readonly Regex SpotifyTrack = Rx(@"(?:open\.spotify\.com/(?:intl-[a-z-]+/)?track/|spotify:track:)([A-Za-z0-9]{22})\b");
    private static readonly Regex YouTubeList = Rx(@"(?:youtube\.com|youtu\.be)/\S*?[?&]list=([A-Za-z0-9_-]{10,64})\b");
    private static readonly Regex DeezerPlaylist = Rx(@"deezer\.com/(?:[a-z]{2}/)?playlist/(\d{1,15})\b");
    private static readonly Regex Meta = Rx(@"<meta\s+(?:property|name)=""([^""]+)""\s+content=""([^""]*)""");

    public static (ImportKind Kind, string Ref) Detect(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return (ImportKind.None, "");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tracks = lines.Count(l => SpotifyTrack.IsMatch(l));
        if (tracks > 0 && tracks >= lines.Length / 2) return (ImportKind.SpotifyTracks, "");
        if (lines.Length == 1 && SpotifyLink.Match(text) is { Success: true } spotify)
            return ((spotify.Groups[1].Success ? spotify.Groups[1].Value : spotify.Groups[2].Value).ToLowerInvariant() == "album" ? ImportKind.SpotifyAlbum : ImportKind.SpotifyPlaylist, spotify.Groups[3].Value);
        if (lines.Length == 1 && YouTubeList.Match(text) is { Success: true } youtube) return (ImportKind.YouTube, youtube.Groups[1].Value);
        if (lines.Length == 1 && DeezerPlaylist.Match(text) is { Success: true } deezer) return (ImportKind.Deezer, deezer.Groups[1].Value);
        if (lines.Length >= 2 && (Header(SplitCsv(lines[0])) ?? Header(SplitCsv(lines[0], ';'))) is { Title: >= 0 }) return (ImportKind.Csv, "");
        return lines.Length == 1 && Uri.TryCreate(text, UriKind.Absolute, out _) ? (ImportKind.None, "") : (ImportKind.Text, "");
    }

    // The source of a playlist that can be read again later, for re-import: "spotify:playlist:ID" and the like.
    public static string? SourceOf(ImportKind kind, string reference) => kind switch
    {
        ImportKind.SpotifyPlaylist => "spotify:playlist:" + reference,
        ImportKind.SpotifyAlbum => "spotify:album:" + reference,
        ImportKind.YouTube => "youtube:" + reference,
        ImportKind.Deezer => "deezer:" + reference,
        _ => null
    };

    public static (ImportKind Kind, string Ref)? FromSource(string? source) => source?.Split(':', 2) switch
    {
        ["spotify", var rest] when rest.Split(':') is [var kind and ("playlist" or "album"), var id] && Regex.IsMatch(id, @"\A[A-Za-z0-9]{22}\z", RegexOptions.None, TimeSpan.FromSeconds(1))
            => (kind == "album" ? ImportKind.SpotifyAlbum : ImportKind.SpotifyPlaylist, id),
        ["youtube", var id] when Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{10,64}\z", RegexOptions.None, TimeSpan.FromSeconds(1)) => (ImportKind.YouTube, id),
        ["deezer", var id] when Regex.IsMatch(id, @"\A\d{1,15}\z", RegexOptions.None, TimeSpan.FromSeconds(1)) => (ImportKind.Deezer, id),
        _ => null
    };

    public static string Clip(string? text, int length = 200)
    {
        var plain = Regex.Replace(WebUtility.HtmlDecode(text ?? ""), @"[\u0000-\u001F<>]", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        return plain.Length > length ? plain[..length] : plain;
    }

    // Spotify's embed player page carries the list (title, artists, length) as JSON. It stops at 100 songs; the playlist
    // page says how many there really are.
    public static (string Name, List<ImportTrack> Tracks) ParseSpotifyEmbed(string html)
    {
        var json = Regex.Match(html, @"<script id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        if (!json.Success) throw new ProviderException("Spotify did not send the playlist.");
        using var document = JsonDocument.Parse(json.Groups[1].Value);
        if (!Path(document.RootElement, "props", "pageProps", "state", "data", "entity", out var entity)) throw new ProviderException("Spotify does not share this playlist.");
        var tracks = new List<ImportTrack>();
        if (entity.TryGetProperty("trackList", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
            {
                if (Text(item, "title") is not { Length: > 0 } title || Text(item, "subtitle") is not { Length: > 0 } subtitle) continue;
                // Artists are separated by a comma and a no-break space, so "Earth, Wind & Fire" stays whole.
                var artist = subtitle.Split(",\u00A0")[0].Replace('\u00A0', ' ').Trim();
                int? ms = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) && n > 0 ? n : null;
                tracks.Add(new ImportTrack(Clip(title), Clip(artist), null, ms, null));
            }
        return (Clip(Text(entity, "name") ?? Text(entity, "title") ?? "Spotify playlist"), tracks);
    }

    public static int? SpotifyTotal(string html)
    {
        var state = Regex.Match(html, @"<script id=""initialState"" type=""text/plain"">([A-Za-z0-9+/=]+)</script>", RegexOptions.None, TimeSpan.FromSeconds(2));
        if (!state.Success) return null;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(state.Groups[1].Value));
            return Regex.Match(text, @"""totalCount"":(\d{1,6})", RegexOptions.None, TimeSpan.FromSeconds(2)) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }
        catch (FormatException) { return null; }
    }

    public static List<string> SpotifyTrackIds(string text) => SpotifyTrack.Matches(text).Select(m => m.Groups[1].Value).Distinct().Take(TrackLimit).ToList();

    // A Spotify song page describes itself in meta tags: title, "Artist · Album · Song · Year", length in seconds.
    public static ImportTrack? ParseSpotifyTrackPage(string html)
    {
        var meta = Meta.Matches(html).GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups[2].Value));
        if (meta.GetValueOrDefault("og:title") is not { Length: > 0 } title) return null;
        var parts = meta.GetValueOrDefault("og:description")?.Split(" · ") ?? [];
        // The whole artist line is kept ("Earth, Wind & Fire" or "A, B"); matching tries it whole, then its first name.
        var artist = meta.GetValueOrDefault("music:musician_description") is { Length: > 0 } named ? named : parts.FirstOrDefault();
        if (artist is not { Length: > 0 }) return null;
        int? ms = int.TryParse(meta.GetValueOrDefault("music:duration"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds * 1000 : null;
        return new ImportTrack(Clip(title), Clip(artist.Trim()), parts.Length >= 3 ? Clip(parts[1]) : null, ms, null);
    }

    // yt-dlp's flat listing of a YouTube or YouTube Music playlist, one tab-separated line per video: playlist title,
    // video title, channel, length in seconds. Music uploads come from "Artist - Topic" channels with the bare title;
    // other videos usually say "Artist - Title".
    public static (string Name, List<ImportTrack> Tracks) ParseYouTube(string lines)
    {
        var name = "YouTube playlist";
        var tracks = new List<ImportTrack>();
        foreach (var line in lines.Split('\n').Take(TrackLimit))
        {
            if (line.TrimEnd('\r').Split('\t') is not [var playlist, var title, var channel, var length]) continue;
            if (playlist is { Length: > 0 } and not "NA") name = playlist;
            if (title.Length == 0 || title is "NA" or "[Private video]" or "[Deleted video]") continue;
            channel = channel is "NA" ? "" : channel.Trim();
            int? ms = double.TryParse(length, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? (int)(seconds * 1000) : null;
            var bare = title.Split(" | ")[0];
            var (artist, song) = channel.EndsWith(" - Topic", StringComparison.Ordinal) ? (channel[..^8], title) : SplitArtist(bare) ?? (channel, bare);
            if (artist.Length == 0) continue;
            tracks.Add(new ImportTrack(Clip(CleanVideoTitle(song)), Clip(artist), null, ms, null, Clip(CleanVideoTitle(title) + " " + channel)));
        }
        return (Clip(name), tracks);
    }

    // Video titles carry extras after a "|" (film, cast, label) and words like "(Official Video)" or "Visualiser".
    public static string CleanVideoTitle(string title)
    {
        var first = title.Split(" | ")[0];
        var clean = Regex.Replace(first, @"\s*[(\[]?\s*(?:official\s+)?(?:music\s+|lyric(?:al)?\s+|full\s+)?(?:video(?:\s+klip)?|audio|lyrics?|visuali[sz]er|mv|hd|4k|full\s+song)\s*[)\]]?\s*\z", "",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        clean = Regex.Replace(clean, @"\s*[(\[](?:official\s+)?(?:music\s+)?(?:video|audio|lyrics?|lyric video|visuali[sz]er|hd|4k)[)\]]\s*", " ", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Trim();
        return clean.Length > 0 ? clean : title.Trim();
    }

    // A Deezer playlist page of its tracks; ISRCs make these exact.
    public static (List<ImportTrack> Tracks, bool More) ParseDeezerTracks(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _)) throw new ProviderException("Deezer does not share this playlist.");
        var tracks = new List<ImportTrack>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var t in data.EnumerateArray())
            {
                if (Text(t, "title") is not { Length: > 0 } title || !t.TryGetProperty("artist", out var artist) || Text(artist, "name") is not { Length: > 0 } name) continue;
                var album = t.TryGetProperty("album", out var a) ? Text(a, "title") : null;
                int? ms = t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var s) && s > 0 ? s * 1000 : null;
                var isrc = Text(t, "isrc")?.ToUpperInvariant();
                tracks.Add(new ImportTrack(Clip(title), Clip(name), album is null ? null : Clip(album), ms, isrc is { Length: 12 } ? isrc : null));
            }
        return (tracks, root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String);
    }

    public static string DeezerName(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("error", out _)) throw new ProviderException("Deezer does not share this playlist.");
        return Clip(Text(document.RootElement, "title") ?? "Deezer playlist");
    }

    // "Artist - Title" (or with an en dash), one song per line. Numbering like "1. " in front is ignored.
    public static List<ImportTrack> ParseText(string text) => text.Split('\n').Select(line => Regex.Replace(line.Trim(), @"\A\d{1,4}[.)]\s+", "", RegexOptions.None, TimeSpan.FromSeconds(1)))
        .Select(line => SplitArtist(line) is { } pair ? new ImportTrack(Clip(pair.Title), Clip(pair.Artist), null, null, null) : null).OfType<ImportTrack>().Take(TrackLimit).ToList();

    private static (string Artist, string Title)? SplitArtist(string line)
    {
        var m = Regex.Match(line, @"\A(.+?)\s+[-–]\s+(.+)\z", RegexOptions.None, TimeSpan.FromSeconds(1));
        return m.Success && m.Groups[1].Value.Trim().Length > 0 && m.Groups[2].Value.Trim().Length > 0 ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : null;
    }

    // Exportify, TuneMyMusic, Soundiiz and similar exports: a header row names the columns.
    public sealed record CsvColumns(int Title, int Artist, int Album, int Duration, int Isrc);
    public static CsvColumns? Header(IReadOnlyList<string> header)
    {
        int Find(params string[] names)
        {
            for (var i = 0; i < header.Count; i++) if (names.Contains(header[i].Trim().Trim('"').TrimStart('\uFEFF').ToLowerInvariant())) return i;
            return -1;
        }
        var title = Find("track name", "track", "title", "song", "song name", "name");
        var artist = Find("artist name(s)", "artist name", "artist", "artists", "artist(s)");
        return title < 0 || artist < 0 ? null : new CsvColumns(title, artist, Find("album name", "album"), Find("duration (ms)", "duration_ms", "duration"), Find("isrc"));
    }

    public static List<ImportTrack> ParseCsv(string text)
    {
        var rows = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (rows.Count < 2) return [];
        // The separator is the header's: a semicolon file keeps commas inside its cells.
        var separator = rows[0].Count(c => c == ';') > rows[0].Count(c => c == ',') ? ';' : ',';
        if (Header(SplitCsv(rows[0], separator)) is not { } columns) return [];
        var tracks = new List<ImportTrack>();
        foreach (var row in rows.Skip(1).Take(TrackLimit))
        {
            var cells = SplitCsv(row, separator);
            string? Cell(int at) => at >= 0 && at < cells.Count && cells[at].Trim() is { Length: > 0 } value ? value : null;
            if (Cell(columns.Title) is not { } title || Cell(columns.Artist) is not { } artists) continue;
            var duration = Cell(columns.Duration) is { } d && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0
                ? (int?)(number > 36000 ? number : number * 1000) : null;
            var isrc = Cell(columns.Isrc)?.ToUpperInvariant();
            tracks.Add(new ImportTrack(Clip(title), Clip(artists.Trim()), Cell(columns.Album) is { } album ? Clip(album) : null, duration, isrc is { Length: 12 } ? isrc : null));
        }
        return tracks;
    }

    public static List<string> SplitCsv(string line, char separator = ',')
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == separator) { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    // Spotify's "Download your data" export: Playlist1.json (your playlists) and YourLibrary.json (Liked Songs).
    public static List<ImportList> ParseSpotifyExport(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        var lists = new List<ImportList>();
        // Other files from the same export (StreamingHistory, a JSON array) are not playlists.
        if (root.ValueKind != JsonValueKind.Object) throw new ProviderException("This file is not a Spotify playlist export. Use Playlist1.json or YourLibrary.json from Spotify's \"Download your data\".");
        if (root.TryGetProperty("playlists", out var playlists) && playlists.ValueKind == JsonValueKind.Array)
            foreach (var playlist in playlists.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Take(MusicLibraryStore.PlaylistLimit))
            {
                var tracks = new List<ImportTrack>();
                if (playlist.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var item in items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Take(TrackLimit))
                        if (item.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object && Text(t, "trackName") is { Length: > 0 } title && Text(t, "artistName") is { Length: > 0 } artist)
                            tracks.Add(new ImportTrack(Clip(title), Clip(artist), Text(t, "albumName") is { } album ? Clip(album) : null, null, null));
                lists.Add(new ImportList(Clip(Text(playlist, "name") ?? "Spotify playlist"), tracks, null));
            }
        if (root.TryGetProperty("tracks", out var liked) && liked.ValueKind == JsonValueKind.Array)
        {
            var tracks = liked.EnumerateArray().Take(TrackLimit).Select(t => Text(t, "track") is { Length: > 0 } title && Text(t, "artist") is { Length: > 0 } artist
                ? new ImportTrack(Clip(title), Clip(artist), Text(t, "album") is { } album ? Clip(album) : null, null, null) : null).OfType<ImportTrack>().ToList();
            if (tracks.Count > 0) lists.Add(new ImportList("Liked Songs", tracks, null));
        }
        if (lists.Count == 0) throw new ProviderException("This file is not a Spotify playlist export. Use Playlist1.json or YourLibrary.json from Spotify's \"Download your data\".");
        return lists;
    }

    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Path(JsonElement root, string a, string b, string c, string d, string e, out JsonElement found)
    {
        found = root;
        foreach (var key in new[] { a, b, c, d, e })
            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(key, out found)) return false;
        return found.ValueKind == JsonValueKind.Object;
    }
}
