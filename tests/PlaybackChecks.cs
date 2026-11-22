using StreamFin;
using static Harness;

// Anime episode fallback: which sites are asked, and how often. Adapted from the fork's PlaybackChecks.
static class PlaybackChecks
{
    public static async Task RunAsync()
    {
        var anime = new Anime(1, "Example", [], 2026, "", null, 24, false, []);
        var config = new Configuration { Enabled = true, ZokoEnabled = false, AnimeGgEnabled = true, AnimeHeavenEnabled = true };
        var calls = new List<string>();
        var registry = new SourceRegistry([new Recording("animegg", calls, fail: true), new Recording("animeheaven", calls)]);

        var played = await registry.ResolveWithFallbackAsync(anime, 2, "gg-2", default, config, listedExact: true);
        Check(played.Key == "animeheaven:episode-2" && calls.SequenceEqual(["animegg:resolve", "animeheaven:search", "animeheaven:episodes", "animeheaven:resolve"]),
            "a failed episode is looked up once on the next site, then played from there");
        calls.Clear();
        await registry.ResolveWithFallbackAsync(anime, 2, "gg-2", default, config, listedExact: true);
        Check(calls.SequenceEqual(["animegg:resolve", "animeheaven:resolve"]), "playing it again reuses the site's title and episode list, with a fresh video link");

        calls.Clear();
        Check((await registry.ResolveWithFallbackAsync(anime, 2, "animeheaven:episode-2", default, config)).Key == "animeheaven:episode-2"
            && calls.SequenceEqual(["animeheaven:resolve"]), "a chosen site that works asks no other site");

        calls.Clear();
        await RejectAsync(() => registry.ResolveWithFallbackAsync(anime, 2, "gg-2", default, new Configuration { Enabled = true, ZokoEnabled = false, AnimeGgEnabled = true, AnimeHeavenEnabled = false }, listedExact: true),
            "a switched-off site is never used as the fallback");
        Check(calls.SequenceEqual(["animegg:resolve"]), "a switched-off site gets no requests");

        calls.Clear();
        var wrongNumber = new SourceRegistry([new Recording("animegg", calls, fail: true), new Recording("animeheaven", calls, number: 3)]);
        await RejectAsync(() => wrongNumber.ResolveWithFallbackAsync(anime, 2, "gg-2", default, config, listedExact: true), "the fallback needs the same episode number");
        Check(!calls.Contains("animeheaven:resolve"), "another episode is never started in its place");
    }

    private sealed class Recording(string id, List<string> calls, bool fail = false, decimal number = 2) : IAnimeSource
    {
        public string Id => id;
        public string Name => id;
        public Task<IReadOnlyList<SourceTitle>> SearchAsync(string title, CancellationToken ct)
        {
            calls.Add(id + ":search");
            return Task.FromResult<IReadOnlyList<SourceTitle>>([new("one", "Example", 2026)]);
        }
        public Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesId, Anime anime, CancellationToken ct)
        {
            calls.Add(id + ":episodes");
            return Task.FromResult<IReadOnlyList<SourceEpisode>>([new("episode-2", number, "Episode")]);
        }
        public Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string episodeId, CancellationToken ct)
        {
            calls.Add(id + ":resolve");
            if (fail) throw new ProviderException("No English subtitles");
            return Task.FromResult<IReadOnlyList<ResolvedVideo>>([new(new Uri("https://example.org/video.mp4"), "1080p", new Dictionary<string, string>(), true)]);
        }
    }
}
