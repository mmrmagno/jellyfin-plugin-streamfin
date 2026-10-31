using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamFin;

// Deezer's public API: no account, about 50 calls per 5 s, and steady. It describes music only (it sends no audio)
// and stands in for the catalog's search, albums and artists while that is down. Its IDs carry a "dz" prefix, so
// they never meet the catalog's own, and every stored star, play and file of a catalog song keeps its ID.
public static class Deezer
{
    public const string Host = "api.deezer.com";
    public const string ImageHost = "cdn-images.dzcdn.net";
    public static readonly Uri Api = new("https://" + Host + "/");

    public static bool IsId(string? id) => id is not null && Regex.IsMatch(id, @"\Adz[1-9][0-9]{0,14}\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    public static string Number(string id) => IsId(id) ? id[2..] : throw new ProviderException("Invalid music identifier.");

    // An error comes back as HTTP 200 with an "error" object: 4 is the rate limit, 700 a busy service, 800 means there
    // is no such thing. Anything else is about the request (an odd query), so it never counts as an outage.
    public static void Check(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new SourceStatusException(502);
        if (!document.RootElement.TryGetProperty("error", out var error)) return;
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : 0;
        throw code switch
        {
            4 => new RateLimitedException(Host + " is rate limiting requests. Please try again in a minute.", TimeSpan.FromSeconds(5)),
            700 => new SourceStatusException(503),
            800 => new SourceStatusException(404),
            _ => new SourceStatusException(400)
        };
    }

    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString()?.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal).Trim() is { Length: > 0 } s ? s : null;
    private static int? Count(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 0 ? n : null;
    private static string? Id(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n is > 0 and < 1_000_000_000_000_000 / 1000 ? "dz" + n.ToString(CultureInfo.InvariantCulture) : null;
    private static JsonElement Field(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
    // Lists come wrapped as {"data": [...]}, some nested one level down (an album's tracks).
    private static IEnumerable<JsonElement> List(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("data", out var data)
        && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];

    // Covers are rebuilt from the image's hash alone, so whatever address the answer carries, only Deezer's image
    // host is ever asked for one.
    public static string? Image(string kind, string? hash) => hash is not null && Regex.IsMatch(hash, @"\A[0-9a-f]{32}\z", RegexOptions.None, TimeSpan.FromSeconds(1))
        ? $"https://{ImageHost}/images/{kind}/{hash}/1000x1000-000000-80-0-0.jpg" : null;
    private static string? Picture(JsonElement e) => Text(e, "picture_xl") is { } url
        && Regex.Match(url, @"\Ahttps://(?:cdn-images|e-cdns-images)\.dzcdn\.net/images/artist/([0-9a-f]{32})/", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } m
        ? Image("artist", m.Groups[1].Value) : null;

    private static MusicArtistRef? Artist(JsonElement e) => Id(e) is { } id && Text(e, "name") is { } name ? new MusicArtistRef(id, name) : null;

    // Contributors name everyone on a song; on an album the main artists come first.
    private static IReadOnlyList<MusicArtistRef> Artists(JsonElement e)
    {
        var contributors = Field(e, "contributors") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().ToArray() : [];
        var main = contributors.Where(c => Text(c, "role") is null or "Main").Select(Artist);
        return main.Prepend(Artist(Field(e, "artist"))).OfType<MusicArtistRef>().DistinctBy(a => a.Id).Take(20).ToArray();
    }

    public static MusicTrack? ParseTrack(JsonElement e, string? releaseId = null, string? artwork = null)
    {
        if (Id(e) is not { } id || Text(e, "title") is not { } title) return null;
        var album = Field(e, "album");
        if ((IsId(releaseId) ? releaseId : Id(album)) is not { } release) return null;
        var isrc = Text(e, "isrc") is { } code && Regex.IsMatch(code, @"\A[A-Z0-9]{12}\z", RegexOptions.None, TimeSpan.FromSeconds(1)) ? code : null;
        var seconds = Count(e, "duration");
        return new MusicTrack(id, title, Artists(e), release, Image("cover", Text(album, "md5_image")) ?? artwork ?? Image("cover", Text(e, "md5_image")),
            seconds is > 0 and < 86400 ? seconds * 1000 : null, isrc, Field(e, "explicit_lyrics").ValueKind == JsonValueKind.True,
            Count(e, "disk_number") is int disc and > 0 and < 100 ? disc : null, Count(e, "track_position") is int number and > 0 and < 1000 ? number : null);
    }

    public static MusicRelease? ParseAlbum(JsonElement e, IEnumerable<JsonElement>? tracks = null, IReadOnlyList<MusicArtistRef>? artists = null)
    {
        if (Id(e) is not { } id || Text(e, "title") is not { } title) return null;
        var date = DateTime.TryParseExact(Text(e, "release_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) && parsed.Year is > 1800 and < 2200 ? parsed : (DateTime?)null;
        var type = Text(e, "record_type") switch { "single" => "SINGLE", "ep" => "EP", "compile" => "COMPILATION", _ => "ALBUM" };
        var artwork = Image("cover", Text(e, "md5_image"));
        var list = (tracks ?? List(Field(e, "tracks"))).Select(t => ParseTrack(t, id, artwork)).OfType<MusicTrack>().DistinctBy(t => t.Id).Take(500)
            .OrderBy(t => t.Disc ?? 1).ThenBy(t => t.Number ?? int.MaxValue).ToArray();
        var credited = Artists(e);
        return new MusicRelease(id, title, credited.Count > 0 ? credited : artists ?? [], type, date, Text(e, "label"), artwork, Count(e, "nb_tracks"), list);
    }

    private static JsonElement Root(JsonDocument document) => document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement
        : throw new ProviderException("The music catalog returned an unexpected response.");

    // The answers below are several calls put together: {"tracks":..., "albums":..., "artists":...} for a search,
    // {"album":..., "tracks":...} for a release and {"artist":..., "top":..., "albums":...} for an artist.
    public static MusicResults ParseSearch(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = Root(document);
        return new MusicResults(List(Field(root, "tracks")).Select(t => ParseTrack(t)).OfType<MusicTrack>().DistinctBy(t => t.Id).Take(50).ToArray(),
            List(Field(root, "albums")).Select(a => ParseAlbum(a)).OfType<MusicRelease>().DistinctBy(r => r.Id).Take(50).ToArray(),
            List(Field(root, "artists")).Select(a => Artist(a) is { } artist ? new MusicArtistPage(artist.Id, artist.Name, Picture(a), "", [], []) : null)
                .OfType<MusicArtistPage>().DistinctBy(a => a.Id).Take(50).ToArray());
    }

    public static MusicRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = Root(document);
        var tracks = Field(root, "tracks") is { ValueKind: JsonValueKind.Object } listed ? List(listed) : null;
        return ParseAlbum(Field(root, "album"), tracks) ?? throw new ProviderException("This album is unavailable.");
    }

    public static MusicArtistPage ParseArtist(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = Root(document);
        var node = Field(root, "artist");
        if (Artist(node) is not { } artist) throw new ProviderException("This artist is unavailable.");
        var top = List(Field(root, "top")).Select(t => ParseTrack(t)).OfType<MusicTrack>().DistinctBy(t => t.Id).Take(50).ToArray();
        var releases = List(Field(root, "albums")).Select(a => ParseAlbum(a, artists: [artist])).OfType<MusicRelease>().DistinctBy(r => r.Id).Take(200).ToArray();
        return new MusicArtistPage(artist.Id, artist.Name, Picture(node), "", top, releases);
    }
}
