using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamFin;

// HiAnime's own player, the one ani-cli uses. It is addressed by MyAnimeList ID and episode
// number, which AniList already supplies, so no title search or fuzzy matching is involved.
// The page carries its settings as base64 JSON XORed with a key; decode data only. The key is versioned
// ("-v1") and published in the player's own script, so a new one is read from there instead of failing.
public sealed class HiAnimeSource(SafeHttp http) : IDirectAnimeSource
{
    public string Id => "zoko";
    public string Name => "HiAnime";
    public static readonly Uri Origin = new("https://zokoanime.video/");
    public static readonly byte[] DefaultKey = "otaku-embed-v1"u8.ToArray();
    private byte[] key = DefaultKey;
    private DateTime keyChecked;
    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string> { ["Referer"] = Origin.AbsoluteUri };

    public SourceTitle? Direct(Anime anime) => anime.MalId is > 0 ? new(anime.MalId.Value.ToString(CultureInfo.InvariantCulture), anime.Title, anime.Year) : null;
    public Task<IReadOnlyList<SourceTitle>> SearchAsync(string title, CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceTitle>>([]);

    public static void ValidateId(string id, bool episode = false)
    {
        if (!Regex.IsMatch(id, episode ? @"\A[1-9][0-9]{0,8}-[1-9][0-9]{0,4}\z" : @"\A[1-9][0-9]{0,8}\z", RegexOptions.None, TimeSpan.FromSeconds(1)))
            throw new ProviderException("Invalid HiAnime identifier.");
    }

    public Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesId, Anime anime, CancellationToken ct)
    {
        ValidateId(seriesId);
        if (anime.MalId?.ToString(CultureInfo.InvariantCulture) != seriesId) throw new ProviderException("This HiAnime entry belongs to another title.");
        if (anime.AvailableEpisodes is not (> 0 and <= 5000))
            throw new ProviderException("AniList does not list an episode count for this title yet. Try another provider.");
        return Task.FromResult<IReadOnlyList<SourceEpisode>>(Enumerable.Range(1, anime.AvailableEpisodes.Value)
            .Select(n => new SourceEpisode($"{seriesId}-{n}", n, "Episode " + n.ToString(CultureInfo.InvariantCulture))).ToArray());
    }

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string episodeId, CancellationToken ct)
    {
        ValidateId(episodeId, episode: true);
        var (mal, number) = (episodeId[..episodeId.IndexOf('-')], episodeId[(episodeId.IndexOf('-') + 1)..]);
        var html = await http.TextAsync(new Uri(Origin, $"stream/mal/{mal}/{number}/sub"), ct);
        try { return ParseEmbed(html, key); }
        catch (KeyChangedException) when (DateTime.UtcNow - keyChecked > TimeSpan.FromMinutes(10))
        {
            keyChecked = DateTime.UtcNow;
            if (ParseKey(await http.TextAsync(new Uri(Origin, "core/obfuscate.js"), ct)) is not { } current || current.SequenceEqual(key)) throw;
            key = current;
            return ParseEmbed(html, key);
        }
    }

    public sealed class KeyChangedException() : ProviderException("HiAnime changed how its player page is encoded. Try another provider.");

    public static byte[]? ParseKey(string script)
    {
        var match = Regex.Match(script, @"OBF_KEY\s*=\s*(['""])([\x21-\x7e]{4,64}?)\1", RegexOptions.None, TimeSpan.FromSeconds(1));
        return match.Success ? Encoding.ASCII.GetBytes(match.Groups[2].Value) : null;
    }

    public static string Decode(string blob, byte[]? key = null)
    {
        key ??= DefaultKey;
        byte[] raw;
        try { raw = Convert.FromBase64String(blob); }
        catch (FormatException) { throw new KeyChangedException(); }
        for (var i = 0; i < raw.Length; i++) raw[i] ^= key[i % key.Length];
        return Encoding.UTF8.GetString(raw);
    }

    // The player answers a title or episode it does not have with its own "Error 404" page, still HTTP 200.
    public static bool IsMissing(string html) => Regex.IsMatch(html, @"Error 404|couldn['’]t find anything to play", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    public static IReadOnlyList<ResolvedVideo> ParseEmbed(string html, byte[]? key = null)
    {
        var blob = Regex.Match(html, "window\\.__P\\s*=\\s*\"([A-Za-z0-9+/=]{16,})\"", RegexOptions.None, TimeSpan.FromSeconds(2));
        if (!blob.Success)
            throw new ProviderException(IsMissing(html) ? "HiAnime does not have this episode. Try another provider."
                : "HiAnime changed its player page. Try another provider; StreamFin needs an update for this source.");
        JsonElement root;
        try { root = JsonDocument.Parse(Decode(blob.Groups[1].Value, key)).RootElement; }
        catch (JsonException) { throw new KeyChangedException(); }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("src", out var src) || src.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(src.GetString(), UriKind.Absolute, out var url) || url.Scheme != "https")
            throw new ProviderException("HiAnime did not return a playable video. The source adapter may need an update.");
        SafeHttp.ValidateOpen(url);
        var subtitles = new List<Subtitle>();
        if (root.TryGetProperty("subtitles", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            foreach (var track in tracks.EnumerateArray())
            {
                if (track.ValueKind != JsonValueKind.Object || !track.TryGetProperty("src", out var file) || file.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(file.GetString(), UriKind.Absolute, out var subtitle) || subtitle.Scheme != "https") continue;
                try { SafeHttp.ValidateOpen(subtitle); }
                catch (ProviderException) { continue; }
                var label = track.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "Subtitles";
                var language = track.TryGetProperty("lang", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()! : "";
                subtitles.Add(new(label, Languages.Code(language, label), subtitle));
            }
        // Dubbed audio is never substituted, and an untranslated stream is not a subtitled one.
        if (!subtitles.Any(s => s.Language == "en")) throw new ProviderException("HiAnime has no English subtitles for this episode. Try another provider.");
        return [new ResolvedVideo(url, "up to 1080p", Headers, false, Hls: true, Subtitles: Languages.Arrange(subtitles))];
    }
}

public static class Languages
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["spanish"] = "es", ["espanol"] = "es", ["español"] = "es", ["portuguese"] = "pt", ["french"] = "fr",
        ["german"] = "de", ["italian"] = "it", ["arabic"] = "ar", ["russian"] = "ru", ["japanese"] = "ja", ["korean"] = "ko",
        ["chinese"] = "zh", ["indonesian"] = "id", ["malay"] = "ms", ["thai"] = "th", ["vietnamese"] = "vi", ["turkish"] = "tr",
        ["polish"] = "pl", ["dutch"] = "nl", ["romanian"] = "ro", ["greek"] = "el", ["hindi"] = "hi", ["hebrew"] = "he",
        ["swedish"] = "sv", ["danish"] = "da", ["finnish"] = "fi", ["norwegian"] = "no", ["czech"] = "cs", ["hungarian"] = "hu",
        ["ukrainian"] = "uk", ["persian"] = "fa", ["croatian"] = "hr", ["serbian"] = "sr", ["bulgarian"] = "bg"
    };

    public static string Code(string language, string label)
    {
        var tag = language.Trim().ToLowerInvariant();
        if (Regex.IsMatch(tag, @"\A[a-z]{2}(?:[-_][a-z0-9]{2,4})?\z", RegexOptions.None, TimeSpan.FromSeconds(1))) return tag[..2];
        var word = Regex.Match(label, @"\p{L}+", RegexOptions.None, TimeSpan.FromSeconds(1)).Value;
        return Names.TryGetValue(word, out var code) ? code : "und";
    }

    // English first, at most eight tracks: each one is a separate stream FFmpeg opens.
    public static IReadOnlyList<Subtitle> Arrange(IEnumerable<Subtitle> subtitles) => subtitles
        .OrderBy(s => s.Language == "en" ? 0 : 1).DistinctBy(s => s.Url).Take(8).ToArray();
}
