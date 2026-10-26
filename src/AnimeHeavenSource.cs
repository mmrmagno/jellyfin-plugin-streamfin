using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace StreamFin;

// Adapted from Pal's MIT-licensed Seanime AnimeHeaven adapter. Read data only;
// the episode key cookie is the site's normal gate navigation, not a user session.
public sealed class AnimeHeavenSource : IAnimeSource
{
    public string Id => "animeheaven";
    public string Name => "AnimeHeaven";
    private static readonly Uri Origin = new("https://animeheaven.me/");
    // Observed on the site's player; do not trust arbitrary subdomains from page HTML.
    public static bool IsMediaHost(string host) => host is "ax.animeheaven.me" or "ci.animeheaven.me" or "ct.animeheaven.me" or "ck.animeheaven.me" or "cu.animeheaven.me";
    private static MatchCollection Matches(string html, string pattern) => Regex.Matches(html, pattern,
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    private static string? Attribute(string tag, string name) => Matches(tag, @"\b" + name + @"\s*=\s*['""]([^'""]*)['""]").FirstOrDefault()?.Groups[1].Value;

    public static void ValidateId(string id, bool episode = false)
    {
        if (!Regex.IsMatch(id, episode ? @"\A[a-f0-9]{32}\z" : @"\A[a-z0-9]{1,40}\z", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            throw new ProviderException("Invalid AnimeHeaven identifier.");
    }

    public async Task<IReadOnlyList<SourceTitle>> SearchAsync(string title, CancellationToken ct)
        => ParseSearch(await AnimeHeavenPages.TextAsync(new Uri(Origin, "search.php?s=" + Uri.EscapeDataString(title)), ct));

    public static IReadOnlyList<SourceTitle> ParseSearch(string html)
    {
        var results = new List<SourceTitle>();
        foreach (Match match in Matches(html, @"<a\b(?<a>[^>]*)>\s*<img\b(?<img>[^>]*)>"))
        {
            var href = Attribute(match.Groups["a"].Value, "href");
            var title = Attribute(match.Groups["img"].Value, "alt");
            if (href?.StartsWith("anime.php?", StringComparison.Ordinal) != true || string.IsNullOrWhiteSpace(title)) continue;
            var id = href[10..];
            ValidateId(id);
            var clean = AnimeGgSource.Clean(title);
            // Dubbed entries, if added by the site, must not silently replace subtitles.
            if (Regex.IsMatch(clean, @"\b(?:dub|dubbed)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) continue;
            results.Add(new(id, clean));
        }
        return results.DistinctBy(t => t.Id).ToArray();
    }

    public async Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesId, Anime anime, CancellationToken ct)
    {
        ValidateId(seriesId);
        var html = await AnimeHeavenPages.TextAsync(new Uri(Origin, "anime.php?" + seriesId), ct);
        var episodes = ParseEpisodes(html);
        if (episodes.Count == 0) throw new ProviderException("No episodes were found on AnimeHeaven for this title.");
        return episodes;
    }

    public static IReadOnlyList<SourceEpisode> ParseEpisodes(string html)
    {
        var episodes = new List<SourceEpisode>();
        foreach (Match anchor in Matches(html, @"<a\b(?<attrs>[^>]*)>(?<body>.*?)</a>"))
        {
            var id = Attribute(anchor.Groups["attrs"].Value, "id");
            if (id is null) continue;
            var number = Matches(anchor.Groups["body"].Value, @"<div\b[^>]*\bwatch2\b[^>]*>\s*(\d+(?:\.\d+)?)\s*</div>").FirstOrDefault();
            if (number is null) continue;
            ValidateId(id, episode: true);
            if (!decimal.TryParse(number.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) || n > int.MaxValue) continue;
            episodes.Add(new(id, n, "Episode " + n.ToString(CultureInfo.InvariantCulture)));
        }
        return episodes.DistinctBy(e => e.Id).OrderBy(e => e.Number).ToArray();
    }

    public static IReadOnlyList<ResolvedVideo> ParseVideos(string html)
    {
        var urls = Matches(html, @"<source\b([^>]*)>").Select(m => Attribute(m.Groups[1].Value, "src")).OfType<string>().ToArray();
        if (urls.Length == 0) throw new ProviderException("AnimeHeaven did not return a playable video. Please try the other provider.");
        var videos = new List<ResolvedVideo>();
        foreach (var value in urls)
        {
            if (!Uri.TryCreate(Origin, WebUtility.HtmlDecode(value), out var url) || !IsMediaHost(url.IdnHost) || url.AbsolutePath != "/video.mp4") continue;
            try { SafeHttp.Validate(url, true); }
            catch (ProviderException) { continue; }
            videos.Add(new ResolvedVideo(url, "source quality", new Dictionary<string, string>
                { ["Referer"] = Origin.AbsoluteUri, ["Origin"] = "https://animeheaven.me" }, true));
        }
        // The player rotates its primary CDN and includes backup hosts. An unreviewed
        // primary must not invalidate a known backup; never request the unknown link.
        if (videos.Count == 0) throw new ProviderException("AnimeHeaven returned no supported video destinations. Please try the other provider.");
        return videos.DistinctBy(v => v.Url).ToArray();
    }

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string episodeId, CancellationToken ct)
    {
        ValidateId(episodeId, episode: true);
        var headers = new Dictionary<string, string> { ["Cookie"] = "key=" + episodeId, ["Referer"] = "https://animeheaven.me/anime.php" };
        return ParseVideos(await AnimeHeavenPages.TextAsync(new Uri(Origin, "gate.php"), ct, headers));
    }
}
