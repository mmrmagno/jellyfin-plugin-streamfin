using System.Net;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using StreamFin;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }
void Reject(Action action, string name) { try { action(); } catch (ProviderException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
Exception? Throws(Action action) { try { action(); } catch (Exception ex) { return ex; } return null; }
void Contained(Action action, string name) { try { action(); } catch (ProviderException) { } Check(true, name); }
async Task RejectAsync(Func<Task> action, string name) { try { await action(); } catch (ProviderException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }

var titles = AnimeGgSource.ParseSearch("<a href=\"/series/test-show\" class=\"mse\"><h2>Test &amp; Show</h2></a><a href=\"/series/test-show\" class=\"mse\"><h2>Duplicate</h2></a>");
Check(titles.Count == 1 && titles[0].Title == "Test & Show", "search decodes entities and deduplicates");
var episodes = AnimeGgSource.ParseEpisodes("<a href=\"/test-episode-10\" class=\"anm_det_pop\"><strong>10</strong><i class=\"anititle\">Ten</i></a><a href=\"/test-episode-2\" class=\"anm_det_pop\"><strong>2</strong><i class=\"anititle\">Two</i></a><a href=\"/test-episode-2.5\" class=\"anm_det_pop\"><strong>2.5</strong><i class=\"anititle\">Special</i></a>");
Check(episodes.Select(e => e.Number).SequenceEqual(new decimal[] { 2, 2.5m, 10 }), "episodes sort numerically and preserve specials");
Check(AnimeGgSource.ParseEpisodes("<a href=\"/test-episode-" + new string('9', 40) + "\" class=\"anm_det_pop\"><strong>1</strong><i class=\"anititle\">Huge</i></a><a href=\"/test-episode-3\" class=\"anm_det_pop\"><strong>3</strong><i class=\"anititle\">Three</i></a>")
    .Single().Number == 3, "an episode number too large to be real is skipped, not a crash");
Reject(() => AnimeGgSource.ParseSubEmbed("<div id=\"dubbed-Animegg\"><iframe src=\"/embed/999\">"), "dub-only episode is rejected");
Check(AnimeGgSource.ParseSubEmbed("<div id=\"subbed-Animegg\" class=\"tab-pane\">\n<iframe src=\"/embed/123\">") == "/embed/123", "selects the subtitled embed");
var videos = AnimeGgSource.ParseVideos("var videoSources = [{file: \"/play/1/video.mp4\", label: \"480p\"},{file: \"/play/2/video.mp4\", label: \"1080p\"}];");
Check(videos[0].Quality == "1080p" && videos[0].Url.Host == "www.animegg.org", "highest source quality and absolute URL");
Reject(() => AnimeGgSource.ParseVideos("<script>throw new Error('not data')</script>"), "malformed embed has an explicit failure");
Reject(() => AnimeGgSource.ParseVideos("var videoSources = [{file: \"https://localhost/private\", label: \"1080p\"}];"), "malicious media destination rejected");
foreach (var id in new[] { "../config", "https://evil.example", "test?override=x", "a/b", "" }) Reject(() => AnimeGgSource.ValidateId(id), "invalid ID " + id);
foreach (var uri in new[] { "http://www.animegg.org/", "https://animegg.org.evil.example/", "https://user@www.animegg.org/", "https://127.0.0.1/", "https://vidcache.net:22/" })
    Reject(() => SafeHttp.Validate(new Uri(uri), true), "unsafe URL " + uri);
foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "172.16.0.1", "192.168.1.1", "169.254.169.254", "100.64.0.1", "::1", "::ffff:127.0.0.1", "fd00::1", "fe80::1" })
    Check(!SafeHttp.IsPublicAddress(IPAddress.Parse(address)), "private address rejected " + address);
Check(SafeHttp.IsPublicAddress(IPAddress.Parse("1.1.1.1")), "public IPv4 accepted");
Check(SafeHttp.IsPublicAddress(IPAddress.Parse("64:ff9b::101:101")) && !SafeHttp.IsPublicAddress(IPAddress.Parse("64:ff9b::a00:1")) && !SafeHttp.IsPublicAddress(IPAddress.Parse("64:ff9b::7f00:1")), "NAT64 judged by its embedded IPv4");
Check(AniListCatalog.CurrentSeason(new DateTime(2026, 10, 1)) == ("FALL", 2026), "October season boundary");
Check(AniListCatalog.CurrentSeason(new DateTime(2027, 1, 1)) == ("WINTER", 2027), "January season boundary");
Check(!Plugin.Allows(Guid.NewGuid()) && !Plugin.Allows(Guid.Empty), "unconfigured plugin denies all accounts");
var ordinary = new Anime(1, "Example", [], 2026, "", null, 24, false, ["Action"]);
Check(AniListCatalog.IsAllowed(ordinary, true, true), "ordinary anime passes both filters");
Check(!AniListCatalog.IsAllowed(ordinary with { IsAdult = true }, true, false), "adult filter excludes explicit titles");
Check(!AniListCatalog.IsAllowed(ordinary with { Genres = ["Ecchi"] }, false, true), "ecchi filter applies independently of adult flag");
Check(AniListCatalog.IsAllowed(ordinary with { Genres = ["Ecchi"] }, true, false), "ecchi option can be disabled separately");
Check(AniListCatalog.IsAllowed(ordinary with { IsAdult = true }, false, false), "filters respect explicitly disabled configuration");
var heavenTitles = AnimeHeavenSource.ParseSearch("""
<div class='similarimg'><a href='anime.php?abc12'><img alt='Test &amp; Show' src='cover'></a></div>
<a href="anime.php?abc12"><img alt="Duplicate"></a><a href='anime.php?dub12'><img alt='Test Show (Dub)'></a>
<a href='advert.php?x'><img alt='Ad'></a>
""");
Check(heavenTitles.Count == 1 && heavenTitles[0] == new SourceTitle("abc12", "Test & Show"), "AnimeHeaven search decodes, deduplicates and excludes dub entries");
var heavenEpisodes = AnimeHeavenSource.ParseEpisodes("""
<a id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" onclick='ignored()'><div class='watch2'>10</div></a>
<a id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'><div class="watch2">2.5</div></a>
<a id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'><div class='watch2'>2.5</div></a>
""");
Check(heavenEpisodes.Select(e => e.Number).SequenceEqual(new[] { 2.5m, 10m }), "AnimeHeaven episodes use stable keys and numeric order");
foreach (var bad in new[] { "../x", "a&other=1", "a;key=evil", "a/b", "", "https://localhost" })
    Reject(() => AnimeHeavenSource.ValidateId(bad), "AnimeHeaven rejects identifier " + bad);
Reject(() => AnimeHeavenSource.ValidateId("abc12", episode: true), "AnimeHeaven gate requires an episode key");
var heavenVideo = AnimeHeavenSource.ParseVideos("""<video><source src='https://co.animeheaven.me/video.mp4?token&amp;expiry' type='video/mp4'></video>""")[0];
Check(heavenVideo.Url.Query == "?token&expiry" && heavenVideo.HardcodedEnglishSubtitles, "AnimeHeaven parses escaped MP4 URL");
Check(heavenVideo.Headers["Referer"] == "https://animeheaven.me/" && heavenVideo.Headers["Origin"] == "https://animeheaven.me", "provider-specific media headers");
Reject(() => AnimeHeavenSource.ParseVideos("<script>execute()</script>"), "AnimeHeaven missing video fails explicitly");
foreach (var bad in new[] { "https://localhost/video.mp4", "http://co.animeheaven.me/video.mp4", "https://co.animeheaven.me:8161/video.mp4", "https://co.animeheaven.me.evil.example/video.mp4", "https://co.animeheaven.me/private", "https://ax.animeheaven.me/video.mp4" })
    Reject(() => AnimeHeavenSource.ParseVideos($"<source src='{bad}'>"), "AnimeHeaven rejects stream " + bad);
var rotated = AnimeHeavenSource.ParseVideos("""<source src='https://unknown.example/video.mp4'><source src='https://ct.animeheaven.me/video.mp4?valid'>""");
Check(rotated.Count == 1 && rotated[0].Url.Host == "ct.animeheaven.me", "unrecognized rotated CDN cannot hide a validated backup");
var decoy = AnimeHeavenSource.ParseVideos("""<source src='https://ax.animeheaven.me/video.mp4?a'><source src='https://po.animeheaven.me/video.mp4?a'>""");
Check(decoy.Count == 1 && decoy[0].Url.Host == "po.animeheaven.me", "AnimeHeaven skips the decoy clip host");
SafeHttp.Validate(heavenVideo.Url, true);
Check(true, "AnimeHeaven CDN permitted for media");
Reject(() => SafeHttp.Validate(heavenVideo.Url, false), "AnimeHeaven CDN excluded from metadata requests");
Reject(() => SafeHttp.Validate(new Uri("https://other.animeheaven.me/video.mp4"), true), "unreviewed AnimeHeaven subdomain denied");
var legacyKey = SourceRegistry.Key("animegg", "cowboy-bebop-episode-1");
Check(legacyKey == "cowboy-bebop-episode-1" && SourceRegistry.Split(legacyKey) == ("animegg", legacyKey), "legacy AnimeGG IDs unchanged");
Check(SourceRegistry.Split(SourceRegistry.Key("animeheaven", "abc12")) == ("animeheaven", "abc12"), "provider keys round trip without collisions");
var config = new Configuration { Enabled = true, AnimeGgEnabled = false, AnimeHeavenEnabled = true };
Check(!SourceRegistry.IsEnabled("animegg", config) && SourceRegistry.IsEnabled("animeheaven", config), "provider enable switches are independent");
Check(!SourceRegistry.IsEnabled("unknown", config) && !SourceRegistry.IsEnabled("animeheaven", new Configuration()), "unknown or globally disabled providers denied");
var matched = await SourceRegistry.MatchAsync(new FakeSource((_, _) => Task.FromResult<IReadOnlyList<SourceTitle>>([new("a", "Example"), new("b", "Example Season 2")])), ordinary, default);
Check(matched.Titles.Count == 1 && matched.Titles[0].Id == "a", "source matching prefers exact title over another season");
var unrelated = await SourceRegistry.MatchAsync(new FakeSource((_, _) => Task.FromResult<IReadOnlyList<SourceTitle>>([new("ad", "Unrelated promoted title")])), ordinary, default);
Check(unrelated.Titles.Count == 0, "unrelated promoted titles cannot become source matches");
var failure = await SourceRegistry.MatchAsync(new FakeSource((_, _) => throw new HttpRequestException("upstream failure")), ordinary, default);
Check(failure.Error is not null && failure.Titles.Count == 0, "one provider failure becomes a source-specific status");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await SourceRegistry.MatchAsync(new FakeSource((_, ct) => Task.FromCanceled<IReadOnlyList<SourceTitle>>(ct)), ordinary, cancelled.Token); throw new Exception("Cancellation swallowed"); }
    catch (OperationCanceledException) { Check(true, "navigation cancellation propagates"); }
}
var redirectResponse = AnimeHeavenPages.ParseResponse("HTTP/1.1 302 Found\r\nLocation: /gate.php\r\n\r\n");
Check(redirectResponse.Status == 302 && redirectResponse.Location == "/gate.php", "curl page redirects are parsed for validation before following");
Check(AnimeHeavenPages.ParseResponse("HTTP/2 200\r\nContent-Type: text/html\r\n\r\n<body>ok</body>").Body == "<body>ok</body>", "curl page headers are separate from body");
Reject(() => AnimeHeavenPages.ParseResponse("<html>not an HTTP response</html>"), "malformed curl response rejected");

var window = new RateWindow(3, TimeSpan.FromMinutes(1));
var t0 = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
Check(Enumerable.Range(0, 3).All(_ => window.Reserve(t0, TimeSpan.FromSeconds(8)) == TimeSpan.Zero), "rate window grants its budget immediately");
Check(window.Reserve(t0, TimeSpan.FromSeconds(8)) is null, "rate window refuses a slot further away than the caller will wait");
Check(window.Reserve(t0, TimeSpan.FromMinutes(2)) == TimeSpan.FromMinutes(1), "rate window schedules the next slot after the oldest expires");
Check(window.Reserve(t0.AddMinutes(1), TimeSpan.FromSeconds(8)) == TimeSpan.Zero, "rate window frees slots as the window slides");
var blocked = new RateWindow(30, TimeSpan.FromMinutes(1));
blocked.Exhaust(t0, TimeSpan.FromSeconds(40));
Check(blocked.Reserve(t0, TimeSpan.FromSeconds(8)) is null && blocked.Reserve(t0.AddSeconds(40), TimeSpan.Zero) == TimeSpan.Zero, "an upstream 429 blocks the budget until Retry-After");

JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
var airing = AniListCatalog.Parse(Json("""{"id":1,"idMal":52991,"title":{"romaji":"Sousou no Frieren","english":"Frieren","native":null},"synonyms":[],"startDate":{"year":2023},"description":"x","coverImage":{"large":"https://s4.anilist.co/a.jpg"},"duration":24,"isAdult":false,"genres":["Drama"],"episodes":28,"nextAiringEpisode":{"episode":11}}"""));
Check(airing.MalId == 52991 && airing.AvailableEpisodes == 10, "airing anime lists episodes up to the next one");
Check(AniListCatalog.Parse(Json("""{"id":2,"idMal":null,"title":{"romaji":"Done"},"synonyms":[],"startDate":{"year":null},"description":null,"coverImage":{"large":null},"duration":null,"isAdult":false,"genres":[],"episodes":12,"nextAiringEpisode":null}""")) is { MalId: null, AvailableEpisodes: 12 }, "finished anime uses its episode total");
Check(AniListCatalog.Parse(Json("""{"id":3,"title":{"romaji":"Soon"},"synonyms":[],"startDate":{"year":2027},"coverImage":{},"isAdult":false,"genres":[],"episodes":12,"nextAiringEpisode":{"episode":1}}""")).AvailableEpisodes is null, "an anime before its first episode lists nothing");

string Blob(string json) { var raw = System.Text.Encoding.UTF8.GetBytes(json); var key = "otaku-embed-v1"u8.ToArray(); for (var i = 0; i < raw.Length; i++) raw[i] ^= key[i % key.Length]; return Convert.ToBase64String(raw); }
var embed = $"<script>window.__P=\"{Blob("""{"src":"https://hls.example.top/p/a/master.m3u8","subtitles":[{"lang":"pt","label":"Portuguese","src":"https://hls.example.top/v/pt.vtt"},{"lang":"en","label":"English","default":true,"src":"https://hls.example.top/v/en.vtt"},{"lang":"en","label":"Local","src":"https://127.0.0.1/x.vtt"}]}""")}\";</script>";
var zoko = HiAnimeSource.ParseEmbed(embed)[0];
Check(zoko.Hls && zoko.Url.AbsoluteUri == "https://hls.example.top/p/a/master.m3u8" && zoko.Headers["Referer"] == "https://zokoanime.video/", "HiAnime player configuration decodes to an HLS stream with its referrer");
Check(zoko.Subtitles!.Select(x => x.Language).SequenceEqual(["en", "pt"]), "HiAnime subtitles put English first and drop private destinations");
Reject(() => HiAnimeSource.ParseEmbed($"<script>window.__P=\"{Blob("""{"src":"https://hls.example.top/m.m3u8","subtitles":[{"lang":"pt","label":"Portuguese","src":"https://hls.example.top/pt.vtt"}]}""")}\";</script>"), "HiAnime without English subtitles is refused");
Reject(() => HiAnimeSource.ParseEmbed($"<script>window.__P=\"{Blob("""{"src":"https://10.0.0.5/m.m3u8","subtitles":[]}""")}\";</script>"), "HiAnime stream on a private address is refused");
Reject(() => HiAnimeSource.ParseEmbed("<html>episode missing</html>"), "HiAnime page without a player fails explicitly");
string Failure(Action action) { try { action(); } catch (ProviderException ex) { return ex.Message; } return ""; }
Check(Failure(() => HiAnimeSource.ParseEmbed("<div class=\"err\">// Error 404</div><p>We couldn't find anything to play here.</p>")).Contains("does not have this episode")
    && Failure(() => HiAnimeSource.ParseEmbed("<html>episode missing</html>")).Contains("changed its player page"), "HiAnime tells a missing episode from a changed player page");
var rotatedKey = "otaku-embed-v2"u8.ToArray();
string BlobWith(byte[] key, string json) { var raw = System.Text.Encoding.UTF8.GetBytes(json); for (var i = 0; i < raw.Length; i++) raw[i] ^= key[i % key.Length]; return Convert.ToBase64String(raw); }
var rotatedPage = $"<script>window.__P=\"{BlobWith(rotatedKey, """{"src":"https://hls.example.top/m.m3u8","subtitles":[{"lang":"en","label":"English","src":"https://hls.example.top/en.vtt"}]}""")}\";</script>";
try { HiAnimeSource.ParseEmbed(rotatedPage); throw new Exception("FAIL: HiAnime with a new key decoded with the old one"); }
catch (HiAnimeSource.KeyChangedException) { Check(true, "HiAnime reports a changed key as such"); }
Check(HiAnimeSource.ParseKey("const OBF_KEY = 'otaku-embed-v2';\nfunction xor(s) {}") is { } read && read.SequenceEqual(rotatedKey)
    && HiAnimeSource.ParseEmbed(rotatedPage, rotatedKey)[0].Url.Host == "hls.example.top", "HiAnime reads a new key from the player script and decodes with it");
Check(HiAnimeSource.ParseKey("const OBF_KEY = \"x\";") is null && HiAnimeSource.ParseKey("const OBF_KEY = 'has space';") is null && HiAnimeSource.ParseKey("") is null, "HiAnime ignores a key that is too short or not plain");
foreach (var bad in new[] { "0", "12a", "1/2", "../1", "123456789012" }) Reject(() => HiAnimeSource.ValidateId(bad), "HiAnime rejects identifier " + bad);
Reject(() => HiAnimeSource.ValidateId("52991", episode: true), "HiAnime episode IDs need an episode number");
var zokoSource = new HiAnimeSource(new SafeHttp());
var direct = await SourceRegistry.MatchAsync(zokoSource, airing, default);
Check(direct.Titles.Single().Id == "52991", "HiAnime matches by MyAnimeList ID without searching");
Check((await SourceRegistry.MatchAsync(zokoSource, ordinary, default)).Error is not null, "HiAnime explains a title without a MyAnimeList entry");
var zokoEpisodes = await zokoSource.EpisodesAsync("52991", airing, default);
Check(zokoEpisodes.Count == 10 && zokoEpisodes[9].Id == "52991-10" && zokoEpisodes[9].Number == 10, "HiAnime lists the aired episodes");
await RejectAsync(() => zokoSource.EpisodesAsync("1", airing, default), "HiAnime refuses an entry that belongs to another title");
await RejectAsync(() => zokoSource.EpisodesAsync("52991", airing with { AvailableEpisodes = null }, default), "HiAnime refuses an unknown episode count");
Check(SourceRegistry.Key("zoko", "52991") == "zoko:52991" && SourceRegistry.Split("zoko:52991-3") == ("zoko", "52991-3"), "HiAnime keys use the provider prefix");
Check(SourceRegistry.IsEnabled("zoko", new Configuration { Enabled = true }) && !SourceRegistry.IsEnabled("zoko", new Configuration { Enabled = true, ZokoEnabled = false }), "HiAnime has its own switch");

Check(Languages.Code("en", "x") == "en" && Languages.Code("pt-BR", "x") == "pt" && Languages.Code("", "English [CC]") == "en" && Languages.Code("", "Klingon") == "und", "subtitle languages map to two-letter codes");
Check(Languages.Arrange(Enumerable.Range(0, 12).Select(i => new Subtitle("L" + i, i == 7 ? "en" : "fr", new Uri($"https://s.example/{i}.vtt")))).Count == 8
    && Languages.Arrange([new Subtitle("F", "fr", new Uri("https://s.example/f.vtt")), new Subtitle("E", "en", new Uri("https://s.example/e.vtt"))])[0].Language == "en", "subtitle tracks are capped with English first");
Check(Languages.Arrange([new Subtitle("Subtitles", "und", new Uri("https://s.example/a.vtt")), new Subtitle("English", "en", new Uri("https://s.example/a.vtt"))]).Single().Language == "en", "a track listed twice keeps its English entry");

var links = new HlsLinks();
var master = Hls.RewriteMaster("""
#EXTM3U
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="Japanese",DEFAULT=YES,URI="audio/ja.m3u8"
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="other",NAME="Dub",URI="audio/en.m3u8"
#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="Old",URI="subs/old.m3u8"
#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360,AUDIO="aud"
360/index.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=5300000,RESOLUTION=1920x1080,AUDIO="aud",SUBTITLES="subs"
https://cdn2.example.top/1080/index.m3u8
#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=100,URI="iframe.m3u8"
""", new Uri("https://cdn.example.top/p/a/master.m3u8"), links);
Check(!master.Contains("cdn") && !master.Contains("Dub") && !master.Contains("Old") && !master.Contains("iframe") && master.Contains("RESOLUTION=1920x1080"), "master keeps only the best variant and the audio it uses");
Check(links.Get(Hls.FirstVariant(master))!.AbsoluteUri == "https://cdn2.example.top/1080/index.m3u8" && links.Get(0)!.AbsoluteUri == "https://cdn.example.top/p/a/audio/ja.m3u8", "relayed links resolve relative and absolute playlist addresses");
Check(!master.Contains("SUBTITLES"), "upstream subtitle renditions are dropped, Jellyfin cannot extract them from a remote stream");
var media = Hls.RewriteMedia("""
#EXTM3U
#EXT-X-TARGETDURATION:10
#EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.top/k.bin"
#EXT-X-MAP:URI="init.mp4"
#EXTINF:9.5,
seg_00000.ts.jpg
#EXTINF:4.25,
https://hls1.example.cfd/s/1080/seg_00001.m4s
#EXT-X-ENDLIST
""", new Uri("https://cdn2.example.top/1080/index.m3u8"), links);
Check(!media.Contains("example") && !media.Contains("p/") && Regex.IsMatch(media, @"METHOD=AES-128,URI=""\d+\.ts""") && Regex.IsMatch(media, @"#EXT-X-MAP:URI=""\d+\.mp4""") && Regex.IsMatch(media, @"\n\d+\.ts\n") && Regex.IsMatch(media, @"\n\d+\.m4s\n"), "media playlists relay keys, init segments and segments as siblings of the playlist, with recognised extensions");
Check(Math.Abs(Hls.Duration(media) - 13.75) < 0.001, "playlist duration sums its segments");
Check(Hls.WebVtt("\uFEFF1\r\n00:00:20,240 --> 00:00:29,210\r\nHello\r\n") == "WEBVTT\n\n1\n00:00:20.240 --> 00:00:29.210\nHello\n" && Hls.WebVtt("WEBVTT\n\n00:20.240 --> 00:29.210\nHi\n").StartsWith("WEBVTT\n\n00:20"), "SubRip subtitles become WebVTT, WebVTT passes through");
Reject(() => Hls.WebVtt("<html>blocked</html>"), "a subtitle URL returning a web page is refused");
Check(Hls.WebVtt(" \nWEBVTT\n\n00:20.240 --> 00:29.210\nHi\n").StartsWith("WEBVTT\n", StringComparison.Ordinal), "WebVTT with leading blank lines starts with its header");
Contained(() => SafeHttp.ValidateOpen(new Uri("https://x\u0627.example.top/")), "a host that breaks IDN rules never escapes as a URI error");
Contained(() => Hls.RewriteMedia("#EXTM3U\n#EXTINF:5,\nhttps://x\u0627.example.top/seg.ts\n", new Uri("https://cdn.example.top/a.m3u8"), links), "a playlist segment on a host that breaks IDN rules never escapes as a URI error");
Reject(() => Hls.RewriteMedia("#EXTM3U\n#EXTINF:5,\nhttps://192.168.1.10/seg.ts\n", new Uri("https://cdn.example.top/a.m3u8"), links), "playlist segment on a private address is refused");
Reject(() => Hls.RewriteMedia("#EXTM3U\n#EXTINF:5,\nfile:///etc/passwd\n", new Uri("https://cdn.example.top/a.m3u8"), links), "playlist segment with a local scheme is refused");
var hostile = Hls.RewriteMedia("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,KEYFORMATURI=\"x\",URI=https://keys.example.top/k\n#EXT-X-FOO:URI=\"https://evil.example.top/a\"\n#EXT-X-PROGRAM-DATE-TIME:2026-01-01\n#EXTINF:5,https://evil.example.top/t\nseg.ts\n", new Uri("https://cdn.example.top/a.m3u8"), links);
Check(!hostile.Contains("example") && !hostile.Contains("FOO") && !hostile.Contains("KEYFORMATURI") && Regex.IsMatch(hostile, @"#EXT-X-KEY:METHOD=AES-128,URI=""\d+\.ts""") && hostile.Contains("#EXTINF:5,\n"), "media playlists keep only rebuilt tags, bare and decoy URIs included");
Reject(() => Hls.RewriteMedia("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",URI=\"http://192.168.1.1/admin\"\n#EXTINF:5,\nseg.ts\n", new Uri("https://cdn.example.top/a.m3u8"), links), "a media playlist that nests renditions is refused");
Reject(() => Hls.RewriteMedia("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nhttps://cdn.example.top/b.m3u8\n", new Uri("https://cdn.example.top/a.m3u8"), links), "a media playlist that nests variants is refused");
Reject(() => Hls.RewriteMedia("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=http://169.254.169.254/latest\n#EXTINF:5,\nseg.ts\n", new Uri("https://cdn.example.top/a.m3u8"), links), "a bare key URI on a private address is refused");
var bare = Hls.RewriteMaster("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=aud,NAME=\"J\",URI=audio.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=10,AUDIO=aud,URI=\"https://evil.example.top/x\"\nv.m3u8\n", new Uri("https://cdn.example.top/m.m3u8"), links);
Check(!bare.Contains("example") && bare.Contains("GROUP-ID=\"aud\"") && bare.Contains("AUDIO=\"aud\""), "master renditions with bare URIs are relayed and stray attributes dropped");
Check(Hls.Attributes("A=1, B=\"x,\\\"y\", A=2,C=bare").SequenceEqual(new Dictionary<string, string> { ["A"] = "2", ["B"] = "x,\"y", ["C"] = "bare" }), "attribute lists parse the way FFmpeg reads them");
Reject(() => new HlsLinks().Add(new Uri("https://cdn.example.top/" + new string('a', 5000)), false), "overlong stream addresses are refused");
Reject(() => FlixSource.Unpack("eval(function(p,a,c,k,e,d){}('" + string.Join(" ", Enumerable.Repeat("0", 5000)) + "',10,1,'" + new string('w', 2000) + "'.split('|')))"), "packed pages that expand without bound are refused");
Check(!AnimeGgSource.Clean("Hi &lt;img src=x onerror=alert(1)&gt; <b>there</b>").Contains('<'), "encoded markup in scraped text cannot become a tag");
Reject(() => Hls.RewriteMaster("<html>blocked</html>", new Uri("https://cdn.example.top/a.m3u8"), links), "non-HLS response is refused");

foreach (var good in new[] { "https://cdn.example.top/a.m3u8", "http://cdn.example.top:8080/a.ts", "https://1.1.1.1/x" }) { SafeHttp.ValidateOpen(new Uri(good)); Check(true, "open policy allows " + good); }
foreach (var bad in new[] { "https://cdn.example.top:22/x", "ftp://cdn.example.top/x", "https://u:p@cdn.example.top/x", "https://10.1.2.3/x", "https://[::1]/x", "https://169.254.169.254/latest" })
    Reject(() => SafeHttp.ValidateOpen(new Uri(bad)), "open policy refuses " + bad);
await RejectAsync(() => new SafeHttp().OpenAsync(new Uri("https://www.animegg.org/"), false, default, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer x" }), "API tokens are only sent to TMDB");
Check(TmdbCatalog.IsValidKey("0123456789abcdef0123456789abcdef") && TmdbCatalog.IsValidKey("eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiJ4In0.signature_part") && !TmdbCatalog.IsValidKey("key&api_key=x"), "TMDB keys are validated before use");

var movie = TmdbCatalog.Parse(Json("""{"id":1290938,"title":"South Park: The End of Obesity","release_date":"2024-05-24","overview":"o","poster_path":"/zx.jpg","adult":false,"runtime":51}"""), FilmKind.Movie)!;
Check(movie is { Kind: FilmKind.Movie, Year: 2024, Runtime: 51, Image: "https://image.tmdb.org/t/p/w500/zx.jpg" }, "TMDB movie parses title, year, runtime and poster");
Check(TmdbCatalog.Parse(Json("""{"id":2190,"name":"South Park","first_air_date":"1997-08-13","media_type":"tv","poster_path":"/../evil.jpg","episode_run_time":[22]}"""), null) is { Kind: FilmKind.Tv, Year: 1997, Runtime: 22, Image: null }, "TMDB TV parses from trending and refuses odd image paths");
Check(TmdbCatalog.Parse(Json("""{"id":5,"name":"A Person","media_type":"person"}"""), null) is null, "TMDB people are skipped in mixed search results");
Check(TmdbCatalog.Parse(Json("""{"id":7,"name":"Long","media_type":"tv","episode_run_time":[99999999999]}"""), null) is { Runtime: null }, "TMDB runtimes outside int range are dropped, not thrown");

Check(FlixSource.Slug("South Park: The End of Obesity") == "south-park-the-end-of-obesity" && FlixSource.Slug("Bob's Burgers & Friends") == "bobs-burgers-and-friends", "FlixHQ slugs follow the site's naming");
var watchHtml = """<a href="https://flixhqz.net/watch-movie/south-park-the-end-of-obesity-2024-watch-online/" class="x"></a><a href="https://flixhqz.net/watch-series/south-park-1997-watch-online/"></a><a href="https://flixhqz.net/watch-movie/south-park-bigger-longer-uncut-1999-watch-online/"></a>""";
Check(FlixSource.MatchWatch(watchHtml, movie)?.AbsolutePath == "/watch-movie/south-park-the-end-of-obesity-2024-watch-online/", "flixhqz.net matches a movie by slug and year");
Check(FlixSource.MatchWatch(watchHtml, new Film(2190, FilmKind.Tv, "South Park", 1998, "", null, null, false))?.AbsolutePath == "/watch-series/south-park-1997-watch-online/", "flixhqz.net tolerates a one-year difference");
Check(FlixSource.MatchWatch(watchHtml, movie with { Title = "South Park" }) is null, "flixhqz.net never substitutes a different title");
var classicHtml = """<div class="flw-item"><a href="https://flixhq.ws/movie/south-park-the-end-of-obesity-70928/" title="South Park: The End Of Obesity" class="film-poster-ahref"></a><div class="fd-infor"><span class="fdi-item">2024</span></div><div class="clearfix"></div>""";
Check(FlixSource.MatchClassic(classicHtml, movie)?.AbsolutePath == "/movie/south-park-the-end-of-obesity-70928/" && FlixSource.MatchClassic(classicHtml, movie with { Year = 2010 }) is null, "flixhq.ws matches title and year");
var players = FlixSource.ParsePlayers("""[{"name":"Vidmoly","link":"https://vidmoly.net/embed-x.html"},{"name":"Bad","link":"javascript:alert(1)"},{"name":"VidSrc","link":"https://vidsrc.example/e/1"}]""");
Check(players.Count == 2 && players.OrderBy(p => FlixSource.Rank(p.Name)).First().Name == "Vidmoly", "FlixHQ player list keeps web links in preference order");
Check(FlixSource.ParseListing("""<a class="sv-item active" data-id="https://vidmoly.net/embed-1.html" data-srv="vidmoly">Vidmoly</a><a class="sv-item" data-id="/relative">x</a>""").Single().Name == "vidmoly", "FlixHQ server listing keeps absolute player links");
var packed = """<script>eval(function(p,a,c,k,e,d){return p}('0 1={2:"3://4.5/6/7.8",9:"a"};',36,11,'var|cfg|file|https|cdn|example|hls|master|m3u8|label|HD'.split('|'),0,{}))</script>""";
Check(FlixSource.Unpack(packed).Single() == "var cfg={file:\"https://cdn.example/hls/master.m3u8\",label:\"HD\"};", "packed player scripts unpack as plain text");
Check(FlixSource.FindMedia(packed)?.AbsoluteUri == "https://cdn.example/hls/master.m3u8", "player stream found inside a packed script");
Check(FlixSource.FindMedia("""file:"https://127.0.0.1/x.m3u8" """) is null, "player stream on a private address is ignored");
Check(FlixSource.FindSubtitles("""tracks:[{file:"https:\/\/s.example\/en.vtt",label:"English"},{file:"https://s.example/t.vtt",label:"thumbnails"}]""").Single().Language == "en", "player subtitle tracks parsed, thumbnails skipped");
Check(FlixSource.FindSubtitles("""tracks:[{file:"https://s.example/en.vtt",label:" "}]""").Count == 0, "player subtitle tracks without a label are skipped");
Check(FlixSource.ParseSubtitleIndex("""[{"file":"https://s.example/es.srt","label":"Spanish"},{"src":"https://s.example/x.vtt","label":""}]""").Single().Language == "es", "subtitle index parsed");
Check(FlixSource.Unbase("zz", 36) == 1295 && FlixSource.Unbase("Z", 62) == 61 && FlixSource.Unbase("!", 36) is null, "packer number bases");

var musicSearch = MusicCatalog.ParseSearch("""
{"tracks":[{"id":"155102871061270528","trackId":"155102871061270528","title":"One <b>More</b> Time","artistIds":["153542153123926016"],"artistNames":["Daft Punk"],
  "releaseId":"155102851176075264","artwork":"https://tracks.monochrome.st/proxy/mi/155102851176075264-1A01.jpg","explicit":false,"playable":true,"duration":320357,"isrc":"GBDUW0000053"},
 {"trackId":"155102871061270529","title":"Unplayable","releaseId":"155102851176075264","playable":false},
 {"trackId":"../etc","title":"Bad id","releaseId":"155102851176075264"},
 {"trackId":"155102871061270530","title":"Elsewhere art","releaseId":"155102851176075264","artwork":"https://evil.example/proxy/mi/x.jpg"}],
 "releases":[{"releaseId":"155102851176075264","title":"Discovery","artistIds":["153542153123926016"],"artistNames":["Daft Punk"],"releaseDate":"2001-03-12T00:00:00.000Z","releaseType":"ALBUM","label":"Daft Life"}],
 "artists":[{"artistId":"153542153123926016","name":"Daft Punk","avatar":"https://tracks.monochrome.st/proxy/c/media/153542153123926016-5A04.jpg"}],"users":[{"id":"x"}]}
""");
Check(musicSearch.Tracks.Count == 2 && musicSearch.Tracks[0].Title == "One bMore/b Time" && musicSearch.Tracks[0].Artists.Single().Name == "Daft Punk"
    && musicSearch.Tracks[0].DurationMs == 320357 && musicSearch.Tracks[0].Isrc == "GBDUW0000053", "music search keeps playable tracks with flat artist lists");
Check(musicSearch.Tracks[0].Artwork!.StartsWith("https://tracks.monochrome.st/proxy/", StringComparison.Ordinal) && musicSearch.Tracks[1].Artwork is null, "music artwork stays on the catalog host");
Check(MusicCatalog.ParseRelease("""{"releaseId":"155102851176075264","title":"x","artwork":"https://@tracks.monochrome.st/proxy/mi/1.jpg"}""").Artwork == "https://tracks.monochrome.st/proxy/mi/1.jpg",
    "music artwork address is rebuilt without an empty user info");
Check(musicSearch.Releases.Single().Date == new DateTime(2001, 3, 12, 0, 0, 0, DateTimeKind.Utc) && musicSearch.Artists.Single().Image is not null, "music search releases and artists");
var musicRelease = MusicCatalog.ParseRelease("""
{"releaseId":"165730834890960896","title":"Box","releaseType":"compilation","artists":[{"artistId":"153542153123926016","name":"Daft Punk"}],
 "tracks":[{"trackId":"165741335931187202","title":"B1","discNumber":2,"trackNumber":1},{"trackId":"165741335931187201","title":"A2","discNumber":1,"trackNumber":2},
  {"trackId":"165741335931187200","title":"A1","discNumber":1,"trackNumber":1,"releaseId":"155102851176075264"},{"trackId":"165741335931187200","title":"Duplicate","discNumber":1,"trackNumber":1}]}
""");
Check(musicRelease.Type == "COMPILATION" && musicRelease.Tracks.Select(t => t.Title).SequenceEqual(["A1", "A2", "B1"]) && musicRelease.Tracks.All(t => t.ReleaseId == "165730834890960896"),
    "music release orders discs and tracks, and its ID overrides a track's own");
var musicArtist = MusicCatalog.ParseArtist("""
{"artistId":"153542153123926016","name":"Daft Punk","bio":"Duo","topTracks":[{"trackId":"287206674229092352","title":"Get Lucky","releaseId":"287206661029617664"}],
 "releases":[{"releaseId":"155102851176075264","title":"Discovery","releaseType":"ALBUM"}],"albums":[{"releaseId":"155102851176075264","title":"Discovery","releaseType":"ALBUM"}],
 "singles":[{"releaseId":"287206661029617664","title":"Get Lucky","releaseType":"SINGLE"}]}
""");
Check(musicArtist.Releases.Count == 2 && musicArtist.TopTracks.Single().Title == "Get Lucky" && musicArtist.Bio == "Duo", "music artist merges release lists without duplicates");
Reject(() => MusicCatalog.ParseRelease("[]"), "music release must be an object");
Reject(() => MusicCatalog.ParseArtist("""{"artistId":"1","name":"x"}"""), "music artist with an invalid ID is rejected");
foreach (var id in new[] { "../x", "1e9", "123", "https://evil.example/1", "155102871061270528/../1", "" }) Reject(() => MusicCatalog.ValidateId(id), "invalid music ID " + id);
Check(MusicCatalog.ParseApi("")!.AbsoluteUri == MusicCatalog.DefaultApi && MusicCatalog.ParseApi(" https://Mirror.Example.org ")!.AbsoluteUri == "https://mirror.example.org/", "music API address default and normalisation");
foreach (var api in new[] { "http://tracks.monochrome.st", "https://tracks.monochrome.st:8443", "https://user@tracks.monochrome.st", "https://1.1.1.1", "https://tracks.monochrome.st/api", "https://tracks.monochrome.st/?x=1", "https://localhost", "file:///etc/passwd" })
    Check(MusicCatalog.ParseApi(api) is null, "unsafe music API address rejected " + api);
Check(MusicCatalog.StreamUri("155102871061270528").AbsoluteUri == "https://tracks.monochrome.st/track/155102871061270528" && SafeHttp.Allowed(MusicCatalog.StreamUri("155102871061270528"), true), "music stream address is a known site");
Check(!SafeHttp.Allowed(new Uri("http://tracks.monochrome.st/track/155102871061270528"), true) && !SafeHttp.Allowed(new Uri("https://tracks.monochrome.st.evil.example/track/1"), true), "music host match is exact and https only");
foreach (var id in new[] { "dz0", "dz", "DZ1", "dz1234567890123456", "dz-1", "dz1/../2", "xdz1" }) Reject(() => MusicCatalog.ValidateId(id), "invalid Deezer ID " + id);
Check(MusicCatalog.IsId("dz3135553") && !MusicCatalog.IsMonochrome("dz3135553") && MusicCatalog.IsMonochrome("155102871061270528"), "Deezer IDs sit beside the catalog's own");
Reject(() => MusicCatalog.StreamUri("dz3135553"), "a Deezer song has no catalog stream of its own");
Check(MusicCatalog.IsStream(MusicCatalog.StreamUri("155102871061270528")) && !MusicCatalog.IsStream(new Uri("https://tracks.monochrome.st/track/dz1"))
    && !MusicCatalog.IsStream(new Uri("https://tracks.monochrome.st/track/155102871061270528?x=1")) && !MusicCatalog.IsStream(new Uri("https://evil.example/track/155102871061270528")),
    "only the catalog's own FLAC address counts as a catalog stream");
var deezerSearch = Deezer.ParseSearch("""
{"tracks":{"data":[{"id":3135553,"readable":true,"title":"One <i>More</i> Time","duration":320,"explicit_lyrics":false,"md5_image":"5718f7c81c27e0b2417e2a4c45224f8a",
   "artist":{"id":27,"name":"Daft Punk"},"album":{"id":302127,"title":"Discovery","md5_image":"5718f7c81c27e0b2417e2a4c45224f8a"}},
  {"id":"3135554","title":"String id","album":{"id":302127}},{"id":1000000000000000,"title":"Too long","album":{"id":302127}},{"id":-1,"title":"Negative","album":{"id":302127}},{"id":3135555,"title":"No album"}],"total":4},
 "albums":{"data":[{"id":302127,"title":"Discovery","md5_image":"../../evil","record_type":"compile","artist":{"id":27,"name":"Daft Punk"}}]},
 "artists":{"data":[{"id":27,"name":"Daft Punk","picture_xl":"https://cdn-images.dzcdn.net/images/artist/638e69b9caaf9f9f3f8826febea7b543/1000x1000-000000-80-0-0.jpg"},
  {"id":28,"name":"Elsewhere","picture_xl":"https://evil.example/images/artist/638e69b9caaf9f9f3f8826febea7b543/1.jpg"}]}}
""");
Check(deezerSearch.Tracks.Single() is { Id: "dz3135553", ReleaseId: "dz302127", Title: "One iMore/i Time", DurationMs: 320000 } t && t.Artists.Single() == new MusicArtistRef("dz27", "Daft Punk")
    && t.Artwork == "https://cdn-images.dzcdn.net/images/cover/5718f7c81c27e0b2417e2a4c45224f8a/1000x1000-000000-80-0-0.jpg", "Deezer search tracks get prefixed IDs, seconds and rebuilt covers");
Check(deezerSearch.Releases.Single() is { Id: "dz302127", Type: "COMPILATION", Artwork: null } && deezerSearch.Artists.Count == 2
    && deezerSearch.Artists[0].Image!.StartsWith("https://cdn-images.dzcdn.net/images/artist/638e69b9", StringComparison.Ordinal) && deezerSearch.Artists[1].Image is null,
    "Deezer covers come only from its image host, built from a plain hash");
var deezerRelease = Deezer.ParseRelease("""
{"album":{"id":302127,"title":"Discovery","md5_image":"5718f7c81c27e0b2417e2a4c45224f8a","label":"Daft Life","nb_tracks":3,"release_date":"2001-03-07","record_type":"album",
  "contributors":[{"id":27,"name":"Daft Punk","role":"Main"},{"id":99,"name":"Guest","role":"Featured"}],"artist":{"id":27,"name":"Daft Punk"},
  "tracks":{"data":[{"id":1,"title":"Embedded","artist":{"id":27,"name":"Daft Punk"}}]}},
 "tracks":{"data":[{"id":3135556,"title":"Harder, Better, Faster, Stronger","isrc":"GBDUW0000059","duration":226,"track_position":4,"disk_number":1,"explicit_lyrics":true,"artist":{"id":27,"name":"Daft Punk"}},
  {"id":3135553,"title":"One More Time","isrc":"bad","duration":320,"track_position":1,"disk_number":1,"artist":{"id":27,"name":"Daft Punk"}}]}}
""");
Check(deezerRelease is { Id: "dz302127", Label: "Daft Life", TrackCount: 3 } && deezerRelease.Date == new DateTime(2001, 3, 7, 0, 0, 0, DateTimeKind.Utc)
    && deezerRelease.Artists.Select(a => a.Id).SequenceEqual(["dz27"]) && deezerRelease.Tracks.Select(t => t.Title).SequenceEqual(["One More Time", "Harder, Better, Faster, Stronger"])
    && deezerRelease.Tracks.All(t => t.ReleaseId == "dz302127" && t.Artwork == deezerRelease.Artwork) && deezerRelease.Tracks[1] is { Isrc: "GBDUW0000059", Explicit: true, Number: 4 }
    && deezerRelease.Tracks[0].Isrc is null, "Deezer release takes its full track list, ordered, main artists only, ISRCs checked");
var deezerArtist = Deezer.ParseArtist("""
{"artist":{"id":27,"name":"Daft Punk"},"top":{"data":[{"id":67238733,"title":"Lose Yourself to Dance","duration":353,"contributors":[{"id":27,"name":"Daft Punk"},{"id":103,"name":"Pharrell Williams"}],
  "artist":{"id":27,"name":"Daft Punk"},"album":{"id":6575789,"md5_image":"311bba0fc112d15f72c8b5a65f0456c1"}}]},
 "albums":{"data":[{"id":302127,"title":"Discovery","record_type":"album"},{"id":302127,"title":"Twice"},{"id":7,"title":"One More Time","record_type":"single"}]}}
""");
Check(deezerArtist.TopTracks.Single().Artists.Select(a => a.Name).SequenceEqual(["Daft Punk", "Pharrell Williams"]) && deezerArtist.Releases.Count == 2
    && deezerArtist.Releases.All(r => r.Artists.Single().Id == "dz27") && deezerArtist.Releases[1].Type == "SINGLE", "Deezer artist page: top tracks credit everyone, albums credit the artist");
Reject(() => Deezer.ParseArtist("""{"artist":{"id":"27","name":"x"}}"""), "Deezer artist without a numeric ID is rejected");
Reject(() => Deezer.ParseRelease("[]"), "Deezer release must be an object");
Check(MusicPaths.Album("/music", deezerRelease with { Id = "dz7" }, edition: true).EndsWith("Discovery (2001) [dz7]", StringComparison.Ordinal), "a short Deezer album ID still names a second edition");
Deezer.Check("""{"data":[]}""");
Check(Throws(() => Deezer.Check("""{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}""")) is RateLimitedException
    && Throws(() => Deezer.Check("""{"error":{"code":800}}""")) is SourceStatusException { Status: 404 }
    && Throws(() => Deezer.Check("""{"error":{"code":700}}""")) is SourceStatusException { Status: 503 }
    && Throws(() => Deezer.Check("""{"error":{"type":"InvalidQueryException","code":600}}""")) is SourceStatusException { Status: 400 }
    && Throws(() => Deezer.Check("""{"error":"odd"}""")) is SourceStatusException { Status: 400 } && Throws(() => Deezer.Check("[1]")) is SourceStatusException,
    "Deezer errors sent as HTTP 200 become rate limits, missing items and outages");
var deezerSong = deezerRelease.Tracks[1];
var monoTwin = new MusicTrack("155102871061270535", "Harder, Better, Faster, Stronger", [new("153542153123926016", "Daft Punk")], "155102851176075264", null, 224693, "GBDUW0000059", false, 1, 4);
Check(MusicCatalog.Twin(deezerSong, [deezerSong, monoTwin with { Id = "155102871061270536", Isrc = "XXX000000000" }, monoTwin])?.Id == monoTwin.Id, "a Deezer song finds the catalog's copy by ISRC first");
Check(MusicCatalog.Twin(deezerSong with { Isrc = null }, [monoTwin with { Isrc = null, Title = "Harder Better Faster Stronger" }, monoTwin with { Id = "155102871061270537", Isrc = null, Explicit = true }])?.Id == "155102871061270537"
    && MusicCatalog.Twin(deezerSong with { Isrc = null }, [monoTwin with { Isrc = null, DurationMs = 230000 }]) is null
    && MusicCatalog.Twin(deezerSong with { Isrc = null }, [monoTwin with { Isrc = null, Artists = [new("153542153123926017", "Thomas Bangalter")] }]) is null
    && MusicCatalog.Twin(deezerSong, [deezerSong]) is null, "without an ISRC the copy must match title, artist and length, and be the catalog's own");
Check(SafeHttp.Allowed(new Uri("https://api.deezer.com/search?q=x"), false) && SafeHttp.Allowed(new Uri(deezerSearch.Tracks[0].Artwork!), false)
    && !SafeHttp.Allowed(new Uri("https://e-cdns-images.dzcdn.net.evil.example/x.jpg"), false), "Deezer's API and image host are known sites");
Check(MusicChannel.ParseTrackId("track/in-search/155102851176075264/155102871061270528") == ("155102851176075264", "155102871061270528")
    && MusicChannel.ParseTrackId("track/hit/155102851176075264/../x") is null && MusicChannel.ParseTrackId("album/search/155102851176075264") is null, "music track item IDs");
var explicitTrack = new MusicTrack("154008128176787456", "Not Like Us", [new("153540612384100352", "Kendrick Lamar")], "154007968545771520", null, 274192, "USUG12400910", true, 1, 1);
var cleanTrack = explicitTrack with { Id = "154008124628406272", ReleaseId = "154007968143118336", Isrc = "USUG12408506", Explicit = false };
var otherClean = cleanTrack with { Id = "154008124628406273", Title = "luther", DurationMs = 177598 };
Check(MusicCatalog.WithoutCleanCopies([cleanTrack, explicitTrack, otherClean]).Select(t => t.Id).SequenceEqual([explicitTrack.Id, otherClean.Id]),
    "clean copies give way to the explicit recording, clean-only songs stay");
Check(MusicCatalog.WithoutCleanCopies([explicitTrack, explicitTrack with { Id = "154008128176787457", ReleaseId = "154007968545771521" }]).Count == 1, "a recording on two releases is listed once");
Check(MusicCatalog.SameSong(explicitTrack, cleanTrack with { Title = "Not Like Us (Clean)", DurationMs = 275000 })
    && !MusicCatalog.SameSong(explicitTrack, cleanTrack with { DurationMs = 280000 })
    && !MusicCatalog.SameSong(explicitTrack, cleanTrack with { Artists = [new("153540612384100353", "Someone Else")] }), "explicit twins match on title, artists and length");
var song = new MusicTrack("155102871061270528", "Song", [new("155102851176075265", "Artist")], "155102851176075264", null, 200000, null, true, 1, 1);
var syncedLyrics = MusicLyrics.Parse("""
    [{"id": 2, "trackName": "Song (Live)", "artistName": "Artist", "albumName": "Live", "duration": 260.0, "instrumental": false, "plainLyrics": "Other words", "syncedLyrics": null}, {"id": 1, "trackName": "Song", "artistName": "Artist", "albumName": "Album", "duration": 200.0, "instrumental": false, "plainLyrics": "First line here\n\nSecond verse line\nThird line", "syncedLyrics": "[ar:Artist]\n[00:01.50] First line here\n[00:04.00]\n[00:05.25][01:05.25] Second verse line\n[1:10.3] Third line"}, {"id": 3, "trackName": "Song", "artistName": "Artist", "albumName": "Album", "duration": 200.0, "instrumental": true, "plainLyrics": null, "syncedLyrics": null}]
    """, song, "Album")!;
Check(syncedLyrics.Metadata.IsSynced == true && syncedLyrics.Lyrics.Select(l => (l.Text, l.Start)).SequenceEqual(new (string, long?)[] {
        ("First line here", 15_000_000), ("", 40_000_000), ("Second verse line", 52_500_000), ("Second verse line", 652_500_000), ("Third line", 703_000_000) }), "lyrics: synced LRC with repeated stamps, tags skipped, a live cut of another length ignored");
var plainLyrics = MusicLyrics.Parse("""{"duration":201,"plainLyrics":"\n<b>One</b>\n\nTwo\n\n","syncedLyrics":null}""", song, "Album")!;
Check(plainLyrics.Metadata.IsSynced == false && plainLyrics.Lyrics.Select(l => l.Text).SequenceEqual(["bOne/b", "", "Two"]) && plainLyrics.Lyrics.All(l => l.Start is null),
    "lyrics: plain text keeps verse breaks, trims padding and drops markup");
Check(MusicLyrics.Parse("""{"duration":200,"instrumental":true}""", song, "Album") is null && MusicLyrics.Parse("[]", song, "Album") is null
    && MusicLyrics.Parse("""{"duration":250,"plainLyrics":"x"}""", song, "Album") is null, "lyrics: instrumentals, empty results and other lengths give none");
static byte[] Flac(params string[] tags)
{
    static byte[] Field(string text) => [.. BitConverter.GetBytes(Encoding.UTF8.GetByteCount(text)), .. Encoding.UTF8.GetBytes(text)];
    byte[] comments = [.. Field("vendor"), .. BitConverter.GetBytes(tags.Length), .. tags.SelectMany(Field)];
    return [.. "fLaC"u8, 0, 0, 0, 34, .. new byte[34], 0x84, (byte)(comments.Length >> 16), (byte)(comments.Length >> 8), (byte)comments.Length, .. comments];
}
var gain = MusicGain.Parse(Flac("TITLE=Song", "REPLAYGAIN_TRACK_GAIN=-5.83 dB", "replaygain_album_gain=-6.83", "REPLAYGAIN_TRACK_PEAK=0.979767", "REPLAYGAIN_ALBUM_PEAK=1.0"));
Check(gain == new ReplayGain(-5.83, -6.83, 0.979767, 1.0), "ReplayGain tags are read from the FLAC Vorbis comment");
var truncated = Flac("REPLAYGAIN_TRACK_GAIN=-5.83 dB");
Check(MusicGain.Parse(Flac("TITLE=Song")) is null && MusicGain.Parse(truncated[..^4]) is null && MusicGain.Parse(Flac("REPLAYGAIN_TRACK_GAIN=999 dB")) is null
    && MusicGain.Parse("ID3x"u8) is null, "ReplayGain: untagged, cut short, absurd values and non-FLAC give none");
byte[] flacHead = [.. "fLaC"u8, 0, 0, 0, 34], mp4Head = [0, 0, 0, 24, .. "ftypdash"u8];
Check(MusicCache.Looks(flacHead, "flac") && MusicCache.Looks(mp4Head, "m4a") && !MusicCache.Looks("<html>  "u8, "flac")
    && !MusicCache.Looks("<html>  "u8, "m4a") && !MusicCache.Looks(flacHead, "m4a") && !MusicCache.Looks(flacHead, "exe"), "the song cache keeps only real FLAC or MP4 files");
var songRoot = Path.Combine(Path.GetTempPath(), "streamfin-songs-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(songRoot);
    foreach (var name in new[] { "155102871061270528.m4a", "155102871061270528.flac", "155102871061270529.m4a", "155102871061270530.exe", "..flac", "155102871061270531.flac.part" })
        File.WriteAllText(Path.Combine(songRoot, name), "x");
    var diskCache = new MusicCache(new SafeHttp(), Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicCache>.Instance, songRoot);
    Check(diskCache.Kept("155102871061270528") == "flac" && diskCache.Kept("155102871061270529") == "m4a" && diskCache.Kept("155102871061270530") is null
        && !File.Exists(Path.Combine(songRoot, "155102871061270528.m4a")) && !File.Exists(Path.Combine(songRoot, "155102871061270531.flac.part"))
        && MusicCache.Container("155102871061270529") == "m4a", "the song cache reads both formats back, a FLAC replaces a lossy copy, other files are ignored");
    using (diskCache.OpenComplete("155102871061270529", "flac")) Check(diskCache.OpenComplete("155102871061270529", "flac") is null, "a lossy copy is never handed out as FLAC");
}
finally { if (Directory.Exists(songRoot)) Directory.Delete(songRoot, true); }
Check(SafeHttp.Allowed(new Uri("https://rr2---sn-qpbpu8-c0qr.googlevideo.com/videoplayback?expire=1"), true)
    && !SafeHttp.Allowed(new Uri("https://rr2---sn-qpbpu8-c0qr.googlevideo.com/videoplayback"), false)
    && !SafeHttp.Allowed(new Uri("http://rr2.googlevideo.com/videoplayback"), true) && !SafeHttp.Allowed(new Uri("https://googlevideo.com.evil.example/x"), true)
    && !SafeHttp.Allowed(new Uri("https://rr2.googlevideo.com:8443/x"), true), "YouTube audio is fetched only over https from its own hosts");
Check(!SafeHttp.Allowed(new Uri("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux"), true)
    && !SafeHttp.Allowed(new Uri("https://release-assets.githubusercontent.com/x"), false), "GitHub is reachable only for the tool download");
Check(MusicAudio.IsAudioAddress(new Uri("https://rr1---sn-x.googlevideo.com/videoplayback?itag=140")) && !MusicAudio.IsAudioAddress(new Uri("https://www.youtube.com/watch?v=x"))
    && !MusicAudio.IsAudioAddress(new Uri("file:///etc/passwd")), "a YouTube audio address must be on googlevideo");
var sums = "e51e69e89489f199530f13d32a4342da3ddb2cf34842f364603a066db05afbf3  yt-dlp\nf5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d  yt-dlp_linux\nbad  yt-dlp_linux_aarch64\n";
Check(YtDlp.Expected(sums, "yt-dlp_linux") == "f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d" && YtDlp.Expected(sums, "yt-dlp_linux_aarch64") is null
    && YtDlp.Expected(sums, "yt-dlp_linux.exe") is null && YtDlp.Expected("E890975BC2549807BBCC0B584FFAFDA72CC7C93349DD99C80483DD784F6C4609 *deno.zip", "deno.zip") == "e890975bc2549807bbcc0b584ffafda72cc7c93349dd99c80483dd784f6c4609",
    "tool checksums are read for exactly the file asked for");
var search = YtDlp.SearchArguments("/data/bin/deno", "-o /tmp/x\n --exec rm");
Check(search[^2] == "--" && search[^1].StartsWith("ytsearch8:-o /tmp/x", StringComparison.Ordinal) && !search[^1].Contains('\n') && search.Contains("--ignore-config")
    && search.Contains("--no-plugin-dirs") && search[search.IndexOf("--js-runtimes") + 1] == "deno:/data/bin/deno", "yt-dlp search text stays one argument after --");
Check(YtDlp.AudioArguments("/d", "JhulBGMA7G4")[^1] == "https://www.youtube.com/watch?v=JhulBGMA7G4", "yt-dlp is asked for one video's audio");
Reject(() => YtDlp.AudioArguments("/d", "--exec=x;rm"), "a YouTube video ID that is not one is refused");
var hbfs = new MusicTrack("155102871061270528", "Harder, Better, Faster, Stronger", [new("155102851176075265", "Daft Punk")], "155102851176075264", null, 224693, null, false, 1, 4);
var hits = MusicAudio.ParseSearch("""
    {"entries":[
      {"id":"gAjR4_CbPpQ","title":"Daft Punk - Harder, Better, Faster, Stronger (Official Video)","channel":"Daft Punk","duration":223},
      {"id":"x84m3YyO2oU","title":"Daft Punk - Harder Better Faster Stronger (Official Live Video 2007)","channel":"Daft Punk","duration":225},
      {"id":"K2cYWfq--Nw","title":"Daft Hands - Harder, Better, Faster, Stronger","channel":"Fr. Eckle Studios","duration":225},
      {"id":"JhulBGMA7G4","title":"Harder, Better, Faster, Stronger","channel":"Daft Punk - Topic","duration":226},
      {"id":"VeauURD-qLk","title":"Daft Punk Greatest Hits","channel":"Music EDM","duration":4275},
      {"id":"bad id","title":"x","duration":224},
      {"id":"yydNF8tuVmU","title":"Daft Punk - Harder, Better, Faster, Stronger (Official Audio)","channel":"Daft Punk","duration":"225"}
    ]}
    """);
Check(hits.Count == 6 && hits.Single(h => h.Id == "yydNF8tuVmU").Duration is null, "YouTube search results are read, with bad IDs and odd durations dropped");
Check(MusicAudio.Best(hits, hbfs)?.Id == "JhulBGMA7G4", "the artist's Topic upload wins over videos, live versions and covers");
Check(MusicAudio.Best(hits.Where(h => h.Id != "JhulBGMA7G4").ToList(), hbfs)?.Id == "gAjR4_CbPpQ", "without one, the artist's own upload of the right length is chosen");
Check(MusicAudio.Best([new("x84m3YyO2oU", "Daft Punk - Harder Better Faster Stronger (Live)", "Daft Punk", 225)], hbfs) is null, "a live version is not taken for the studio recording");
Check(MusicAudio.Best([new("x84m3YyO2oU", "Harder, Better, Faster, Stronger (Live)", "Daft Punk - Topic", 225)], hbfs with { Title = "Harder, Better, Faster, Stronger (Live)" })?.Id == "x84m3YyO2oU",
    "a live recording matches its live upload");
Check(MusicAudio.Best([new("JhulBGMA7G4", "Harder, Better, Faster, Stronger", "Daft Punk - Topic", 229)], hbfs) is null, "a copy more than 3 s longer is not the same recording");
Check(MusicAudio.Best([new("JhulBGMA7G4", "Something Else", "Daft Punk - Topic", 225)], hbfs) is null, "another song of the same length is not taken");
Check(MusicAudio.Best([new("K2cYWfq--Nw", "Daft Hands - Harder, Better, Faster, Stronger", "Fr. Eckle Studios", 225)], hbfs) is null, "an upload that names neither the artist nor their channel is not taken");
Check(MusicAudio.Normalize("Beyoncé & JAY-Z") == "beyonce and jay z" && MusicAudio.Normalize("Simon and Garfunkel") == "simon and garfunkel", "names compare without accents, case or punctuation");
Check(MusicAudio.ParseSearch("[]").Count == 0 && MusicAudio.ParseSearch("""{"entries":{}}""").Count == 0, "odd search answers give no results");
var tagged = Flac("REPLAYGAIN_TRACK_GAIN=-2.5");
Check(MusicGain.AudioStart([.. tagged, 0xFF, 0xF8]) == tagged.Length && MusicGain.AudioStart(tagged[..^1]) is null && MusicGain.AudioStart("ID3x0000"u8) is null,
    "FLAC audio starts after the last metadata block; cut short or not FLAC gives none");
var single = new MusicRelease("155102851176075264", "Single", [new("155102851176075265", "Artist"), new("155102851176075266", "Guest")], "SINGLE", new DateTime(2023, 5, 1), null, null, 1,
    [new MusicTrack("155102871061270528", "Song", [new("155102851176075265", "Artist")], "155102851176075264", null, 1000, null, false, 1, 1)]);
var original = Flac("TITLE=Song (old)", "ARTIST=Artist", "ALBUM=Other Album", "ALBUMARTIST=Someone", "REPLAYGAIN_TRACK_GAIN=-5.6", "REPLAYGAIN_ALBUM_GAIN=-9.1", "REPLAYGAIN_ALBUM_PEAK=1.0",
    "BARCODE=123", "MUSICBRAINZ_ALBUMID=x", "ISRC=GBAYE9701376");
var retagged = MusicTags.Retag([.. original, 0xFF, 0xF8, 1, 2], MusicTags.For(single, single.Tracks[0]))!;
var retaggedText = Encoding.UTF8.GetString(retagged);
Check(MusicGain.AudioStart([.. retagged, 0xFF, 0xF8]) == retagged.Length && retagged.AsSpan(8, 34).SequenceEqual(original.AsSpan(8, 34))
    && MusicGain.Parse(retagged) == new ReplayGain(-5.6, null, null, null), "a saved song keeps its stream info and track gain, and drops another album's gain");
Check(retaggedText.Contains("ALBUM=Single") && retaggedText.Contains("ALBUMARTIST=Artist") && retaggedText.Contains("ALBUMARTIST=Guest")
    && !retaggedText.Contains("Other Album") && !retaggedText.Contains("Someone") && retaggedText.Contains("ARTIST=Artist") && retaggedText.Contains("TRACKNUMBER=1")
    && retaggedText.Contains("DATE=2023-05-01") && !retaggedText.Contains("(old)"), "a saved song is tagged with the release it was saved from");
Check(!retaggedText.Contains("BARCODE") && !retaggedText.Contains("MUSICBRAINZ_ALBUMID") && retaggedText.Contains("ISRC=GBAYE9701376"),
    "another album's own identifiers go, the recording's stay");
static List<int> BlockTypes(byte[] flac)
{
    var types = new List<int>();
    for (var at = 4; at + 4 <= flac.Length; )
    {
        types.Add(flac[at] & 0x7f);
        var last = (flac[at] & 0x80) != 0;
        at += 4 + ((flac[at + 1] << 16) | (flac[at + 2] << 8) | flac[at + 3]);
        if (last) break;
    }
    return types;
}
byte[] withPicture = [.. original[..42], 0x06, 0, 0, 2, 9, 9, .. original[42..]];
Check(MusicTags.Retag(withPicture, MusicTags.For(single, single.Tracks[0])) is { } noPicture && BlockTypes(noPicture).SequenceEqual([0, 4])
    && MusicTags.Retag(withPicture, MusicTags.For(single with { Title = "Other Album" }, single.Tracks[0])) is { } kept && BlockTypes(kept).SequenceEqual([0, 4, 6]),
    "another album's embedded picture goes; the same album's stays");
Check(MusicGain.Parse(MusicTags.Retag(Flac("ALBUM=single", "REPLAYGAIN_ALBUM_GAIN=-3"), MusicTags.For(single, single.Tracks[0]))!)?.AlbumGain == -3,
    "the album gain stays when the song already belonged to that album");
Check(MusicTags.Retag([.. "fLaC"u8, 0, 0, 0, 34, .. new byte[34], .. Enumerable.Range(0, 1200).SelectMany(_ => new byte[] { 1, 0, 0, 0 }), 0x81, 0, 0, 0], []) is null
    && MusicTags.Retag("ID3x0000"u8, []) is null && MusicTags.Retag(original[..^2], []) is null
    && MusicTags.Retag([.. "fLaC"u8, 0x84, 0, 0, 0], []) is null, "retagging refuses what is not a whole FLAC header");
MusicGain.Remember(Flac("REPLAYGAIN_TRACK_GAIN=-2.5"), "155102871061270599", "155102871061270598");
Check(MusicGain.For("155102871061270598")?.TrackGain == -2.5 && MusicGain.For("155102871061270597") is null, "ReplayGain is remembered for the clean and explicit ids");
Check(SafeHttp.Allowed(new Uri("https://lrclib.net/api/get?track_name=x"), false) && !SafeHttp.Allowed(new Uri("https://lrclib.net.evil.example/api/get"), false), "lyrics host is a known site");
Check(SubsonicAuth.Verify("sesame", "26719a1196d2a940705a59634eb18eab", "c19b2d", null) && SubsonicAuth.Verify("sesame", "26719A1196D2A940705A59634EB18EAB", "c19b2d", null)
    && !SubsonicAuth.Verify("sesame", "26719a1196d2a940705a59634eb18eab", "c19b2e", null) && !SubsonicAuth.Verify("sesame", "26719a1196d2a940705a59634eb18eab", "short", null),
    "Subsonic token auth matches the documented vector and needs a real salt");
Check(SubsonicAuth.Verify("sesame", null, null, "sesame") && SubsonicAuth.Verify("sesame", null, null, "enc:736573616d65") && !SubsonicAuth.Verify("sesame", null, null, "enc:zz")
    && !SubsonicAuth.Verify("sesame", null, null, null), "Subsonic plain and hex passwords");
Check(SubsonicController.ParseTrack("tr-155102851176075264-155102871061270528") == ("155102851176075264", "155102871061270528")
    && SubsonicController.ParseTrack("tr-1-2") is null && SubsonicController.ParseTrack("tr-155102851176075264-155102871061270528-x") is null
    && SubsonicController.ParseId("al-155102851176075264", "al") == "155102851176075264" && SubsonicController.ParseId("al-../x", "al") is null
    && SubsonicController.ParseId("ar-155102851176075264", "al") is null && SubsonicController.ParsePlaylist("pl-" + Guid.Empty.ToString("N")) == Guid.Empty
    && SubsonicController.ParsePlaylist("pl-x") is null, "Subsonic IDs");
Check(SubsonicController.ParseRange("bytes=0-1", 1000) == (0, 1) && SubsonicController.ParseRange("bytes=500-", 1000) == (500, 999)
    && SubsonicController.ParseRange("bytes=900-5000", 1000) == (900, 999) && SubsonicController.ParseRange("bytes=-100", 1000) == (900, 999)
    && SubsonicController.ParseRange("bytes=0-1,5-9", 1000) is null && SubsonicController.ParseRange("items=0-1", 1000) is null
    && SubsonicController.ParseRange("bytes=1000-", 1000) is (1000, 999), "converted MP3 ranges: open, clamped, suffix; lists and other units refused, past the end detectable");
Check(SubsonicController.Choose(null, null) == (SubsonicController.AudioFormat.Raw, 0) && SubsonicController.Choose("raw", "128") == (SubsonicController.AudioFormat.Raw, 0)
    && SubsonicController.Choose(null, "128") == (SubsonicController.AudioFormat.Mp3, 128)
    && SubsonicController.Choose("opus", null) == (SubsonicController.AudioFormat.Opus, 160) && SubsonicController.Choose("mp3", "9999") == (SubsonicController.AudioFormat.Mp3, 320)
    && SubsonicController.Choose("flac", "0") == (SubsonicController.AudioFormat.Raw, 0), "Subsonic keeps FLAC unless a lossy format or a low bit rate is asked for");
Check(SubsonicController.Choose("mp3", null, true) == (SubsonicController.AudioFormat.Raw, 0) && SubsonicController.Choose("opus", "1411", true) == (SubsonicController.AudioFormat.Raw, 0)
    && SubsonicController.Choose("mp3", "320", true) == (SubsonicController.AudioFormat.Mp3, 320) && SubsonicController.Choose(null, "128", true) == (SubsonicController.AudioFormat.Mp3, 128),
    "preferring lossless, MP3 without a real bit rate limit gets FLAC; a data saving limit still converts");
var subsonicNode = SubsonicResponse.Root(true).One(new SubsonicNode("album").Set("id", "al-1").Set("songCount", 2).Set("year", null)
    .Add(new SubsonicNode("song").Set("title", "A <\"b\"> & c\u0001").Set("isrc", new[] { "GBDUW0000053" })).Add(new SubsonicNode("song").Set("title", "B")));
var subsonicJson = JsonDocument.Parse(SubsonicResponse.Json(subsonicNode)).RootElement.GetProperty("subsonic-response");
Check(subsonicJson.GetProperty("status").GetString() == "ok" && subsonicJson.GetProperty("openSubsonic").GetBoolean()
    && subsonicJson.GetProperty("album").GetProperty("songCount").GetInt32() == 2 && !subsonicJson.GetProperty("album").TryGetProperty("year", out _)
    && subsonicJson.GetProperty("album").GetProperty("song").GetArrayLength() == 2
    && subsonicJson.GetProperty("album").GetProperty("song")[0].GetProperty("isrc")[0].GetString() == "GBDUW0000053", "Subsonic JSON: fields, arrays for repeated elements, nulls left out");
var subsonicXml = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(SubsonicResponse.Xml(subsonicNode)));
var subsonicNs = System.Xml.Linq.XNamespace.Get("http://subsonic.org/restapi");
Check(subsonicXml.Root!.Name == subsonicNs + "subsonic-response" && subsonicXml.Root.Element(subsonicNs + "album")!.Elements(subsonicNs + "song").Count() == 2
    && subsonicXml.Root.Element(subsonicNs + "album")!.Element(subsonicNs + "song")!.Attribute("title")!.Value == "A <\"b\"> & c",
    "Subsonic XML: attributes escaped, control characters dropped");
var throttledCalls = 0;
HttpResponseMessage Throttled(int ok) => ++throttledCalls >= ok ? new HttpResponseMessage(HttpStatusCode.PartialContent)
    : new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(5)) } };
Check((await SafeHttp.PatientAsync(() => Task.FromResult(Throttled(3)), default)).StatusCode == HttpStatusCode.PartialContent && throttledCalls == 3,
    "a throttled media request is retried until the catalog answers");
throttledCalls = 0;
Check((await SafeHttp.PatientAsync(() => Task.FromResult(Throttled(9)), default)).StatusCode == HttpStatusCode.TooManyRequests && throttledCalls == 3
    && (await SafeHttp.PatientAsync(() => Task.FromResult(Throttled(99)), default, attempts: 1)).StatusCode == HttpStatusCode.TooManyRequests && throttledCalls == 4,
    "throttle retries are bounded, and video gets none");
var musicStoreRoot = Path.Combine(Path.GetTempPath(), "streamfin-music-" + Guid.NewGuid().ToString("N"));
try
{
    var listener = Guid.NewGuid();
    var musicStore = new MusicLibraryStore(musicStoreRoot);
    var played = musicSearch.Tracks[0];
    for (var i = 0; i < MusicLibraryStore.PlayLimit + 20; i++) await musicStore.RecordPlayAsync(listener, played with { Id = (155102871061270528L + i % 3).ToString(System.Globalization.CultureInfo.InvariantCulture) }, "Discovery", default);
    var reloaded = await new MusicLibraryStore(musicStoreRoot).ReadAsync(listener, default);
    Check(reloaded.Plays.Count == MusicLibraryStore.PlayLimit && MusicLibraryStore.Recent(reloaded, 10).Count == 3 && musicStore.Version(listener) > 0, "music history persists, is capped and lists each track once");
    File.WriteAllText(Path.Combine(musicStoreRoot, listener.ToString("N") + ".json"), "{not json");
    Check((await new MusicLibraryStore(musicStoreRoot).ReadAsync(listener, default)).Plays.Count == 0, "damaged music history does not break music");
    Check((await musicStore.ReadAsync(Guid.NewGuid(), default)).Plays.Count == 0, "music history is per user");
    var saver = Guid.NewGuid();
    await musicStore.UpdateAsync(saver, l =>
    {
        l.Saves.Add(new MusicSave("155102851176075264", new string('x', 500), "Artist", MusicSaver.Queued, 0, 10, null, DateTime.UtcNow));
        l.Saves.Add(new MusicSave("155102851176075264", "Duplicate", "Artist", MusicSaver.Saved, 10, 10, null, DateTime.UtcNow));
    }, default);
    var saves = (await new MusicLibraryStore(musicStoreRoot).ReadAsync(saver, default)).Saves;
    Check(saves.Count == 1 && saves[0].State == MusicSaver.Queued && saves[0].Title.Length == 200, "the save queue persists once per album with bounded text");
    File.WriteAllText(Path.Combine(musicStoreRoot, saver.ToString("N") + ".json"),
        """{"Saves":[{"ReleaseId":"../../etc","Title":"x","Artist":"y","State":"queued","Queued":"2026-10-27T00:00:00Z"},{"ReleaseId":"155102851176075264","Title":"x","Artist":"y","State":"run","Queued":"2026-10-27T00:00:00Z"},{"ReleaseId":"155102851176075264","Title":"x","Artist":"y","State":"saved","Queued":"2026-10-27T00:00:00Z"}]}""");
    Check((await new MusicLibraryStore(musicStoreRoot).ReadAsync(saver, default)).Saves.Single().State == MusicSaver.Saved, "a stored save with a bad release ID or state is dropped");
}
finally { if (Directory.Exists(musicStoreRoot)) Directory.Delete(musicStoreRoot, true); }

var breaker = new CatalogBreaker(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20));
var clock = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
breaker.Failure(clock);
breaker.Failure(clock.AddSeconds(1));
Check(!breaker.IsOpen(clock.AddSeconds(1)) && breaker.Allow(clock.AddSeconds(1)), "two catalog failures do not count it as down");
Check(breaker.Failure(clock.AddSeconds(2)) && breaker.IsOpen(clock.AddSeconds(3)) && !breaker.Allow(clock.AddSeconds(3)), "the third failure in a row counts the catalog as down");
Check(breaker.Allow(clock.AddSeconds(23)) && !breaker.Allow(clock.AddSeconds(24)), "while down, one request per period checks whether it is back");
Check(!breaker.Failure(clock.AddSeconds(25)) && !breaker.Allow(clock.AddSeconds(30)), "a failed check keeps it down without logging the outage again");
Check(breaker.Success() && !breaker.IsOpen(clock.AddSeconds(31)) && breaker.Allow(clock.AddSeconds(31)), "one answer brings the catalog back");
var spread = new CatalogBreaker(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20));
spread.Failure(clock);
spread.Failure(clock.AddSeconds(10));
spread.Failure(clock.AddSeconds(45));
Check(!spread.IsOpen(clock.AddSeconds(46)), "failures spread over minutes do not count the catalog as down");
Check(CatalogBreaker.IsOutage(new SourceStatusException(522), default) && CatalogBreaker.IsOutage(new HttpRequestException(), default)
    && CatalogBreaker.IsOutage(new TaskCanceledException(), default) && !CatalogBreaker.IsOutage(new SourceStatusException(404), default)
    && !CatalogBreaker.IsOutage(new RateLimitedException("x", null), default), "timeouts, lost connections and 5xx are outages; 404 and 429 are not");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    Check(!CatalogBreaker.IsOutage(new TaskCanceledException(), cancelled.Token), "a caller giving up is not an outage");
}
var outageCalls = 0;
HttpResponseMessage Flaky(int ok) => ++outageCalls >= ok ? new HttpResponseMessage(HttpStatusCode.PartialContent) : new HttpResponseMessage((HttpStatusCode)522);
Check((await SafeHttp.PatientAsync(() => Task.FromResult(Flaky(2)), default, outages: true)).StatusCode == HttpStatusCode.PartialContent && outageCalls == 2,
    "a media request the catalog answers with 522 is tried again");
outageCalls = 0;
Check((await SafeHttp.PatientAsync(() => Task.FromResult(Flaky(2)), default)).StatusCode == (HttpStatusCode)522 && outageCalls == 1, "without outages, a 5xx is not retried");
outageCalls = 0;
Check((await SafeHttp.PatientAsync(() => ++outageCalls < 2 ? throw new HttpRequestException("reset") : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), default,
    outages: true)).StatusCode == HttpStatusCode.OK && outageCalls == 2, "a dropped connection to the catalog is tried again");

var metaRoot = Path.Combine(Path.GetTempPath(), "streamfin-meta-" + Guid.NewGuid().ToString("N"));
try
{
    var meta = new MusicMetadataStore(Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicMetadataStore>.Instance, metaRoot);
    const string storedRelease = """{"releaseId":"155102851176075264","title":"Stored","tracks":[{"trackId":"155102871061270528","title":"Kept","duration":200000}]}""";
    await meta.WriteAsync("release", "155102851176075264", storedRelease);
    Check(meta.Has("release", "155102851176075264") && meta.Read("release", "155102851176075264") == storedRelease, "catalog answers are kept on disk");
    await meta.WriteAsync("release", "../../etc/passwd", "x");
    await meta.WriteAsync("settings", "155102851176075264", "x");
    Check(meta.Read("release", "../../etc/passwd") is null && !meta.Has("settings", "155102851176075264")
        && Directory.EnumerateFiles(metaRoot, "*", SearchOption.AllDirectories).Count() == 1, "stored metadata is named by kind and validated id only");
    using (var offline = new MusicCatalog(new SafeHttp(), meta))
    {
        var stored = await offline.ReleaseAsync("155102851176075264", default);
        Check(stored.Title == "Stored" && stored.Tracks.Single().Title == "Kept" && offline.HasRelease("155102851176075264"),
            "a stored album answers at once, without waiting for the catalog");
        Check(offline.KnownTrack("155102871061270528")?.Title == "Kept", "songs in a stored album are known by id, for queues");
    }
    const string deezerStored = """{"album":{"id":302127,"title":"From Deezer"},"tracks":{"data":[{"id":3135553,"title":"One More Time","duration":320}]}}""";
    await meta.WriteAsync("release", "dz302127", deezerStored);
    Check(File.Exists(Path.Combine(metaRoot, Deezer.Host, "release", "dz302127.json")), "Deezer answers are kept apart from the catalog's");
    using (var offline = new MusicCatalog(new SafeHttp(), meta))
        Check((await offline.ReleaseAsync("dz302127", default)).Tracks.Single().Id == "dz3135553" && offline.HasRelease("dz302127"), "a stored Deezer album answers through Deezer's parser");
    Directory.CreateDirectory(Path.Combine(metaRoot, MusicCatalog.ApiHost, "release"));
    File.WriteAllText(Path.Combine(metaRoot, MusicCatalog.ApiHost, "release", "155102851176075265.json"), "{not json");
    using (var damaged = new MusicCatalog(new SafeHttp(), meta))
    {
        try { await damaged.ReleaseAsync("155102851176075265", new CancellationTokenSource(TimeSpan.FromMilliseconds(1)).Token); }
        catch (Exception ex) when (ex is ProviderException or OperationCanceledException) { }
        Check(!meta.Has("release", "155102851176075265"), "a damaged stored album is read as missing and removed, not a crash");
    }
}
finally { if (Directory.Exists(metaRoot)) Directory.Delete(metaRoot, true); }

var streamInfo = new byte[42];
"fLaC"u8.CopyTo(streamInfo);
streamInfo[4] = 0x80;
streamInfo[7] = 34;
// 96 kHz, 2 channels, 24 bits: rate 0x17700 over 20 bits, then (channels - 1) and (bits - 1).
streamInfo[18] = 0x17; streamInfo[19] = 0x70; streamInfo[20] = 0x0 << 4 | 1 << 1 | 1; streamInfo[21] = 0x7 << 4;
Check(MusicGain.Format(streamInfo) == new FlacFormat(96000, 24, 2), "the FLAC header gives sample rate, bit depth and channels");
Check(MusicGain.Format(streamInfo.AsSpan(0, 30)) is null && MusicGain.Format("OggS"u8.ToArray().Concat(new byte[40]).ToArray()) is null, "a short or non-FLAC header gives no format");
MusicGain.Remember(streamInfo, "155102871061270599");
Check(MusicGain.FormatFor("155102871061270599")?.SampleRate == 96000, "the format is kept per song even without ReplayGain tags");
foreach (var key in new[] { "tr-155102851176075264-155102871061270528", "al-155102851176075264", "ar-155102851176075264", "tr-dz302127-dz3135553", "al-dz302127", "ar-dz27" })
    Check(MusicLibraryStore.IsRatingKey(key), "rating key " + key);
foreach (var key in new[] { "tr-1-2", "al-../x", "pl-0123", "ar-155102851176075264\n", "", "x-155102851176075264", "tr-dz1", "al-dz1-dz2", "ar-dz0", "tr-dz1-dz2-dz3" })
    Check(!MusicLibraryStore.IsRatingKey(key), "invalid rating key refused " + key.Replace("\n", "\\n"));

var polishRoot = Path.Combine(Path.GetTempPath(), "streamfin-polish-" + Guid.NewGuid().ToString("N"));
try
{
    var keeper = new MusicLibraryStore(polishRoot);
    var listener = Guid.NewGuid();
    var queued = new MusicTrack("155102871061270528", "Kept", [new("155102851176075265", "Artist")], "155102851176075264", null, 200000, null, false, 1, 1);
    await keeper.UpdateAsync(listener, l =>
    {
        l.Ratings["al-155102851176075264"] = 4;
        l.Ratings["../../etc"] = 5;
        l.Ratings["ar-155102851176075265"] = 9;
        l.Queue = new MusicQueue(Enumerable.Repeat(new MusicPlay(queued, "Album", DateTime.UtcNow), MusicLibraryStore.QueueLimit + 5).ToArray(),
            "tr-155102851176075264-155102871061270528", -5, DateTime.UtcNow, new string('c', 500));
        l.Bookmarks.Add(new MusicBookmark(new MusicPlay(queued, "Album", DateTime.UtcNow), 1000, new string('x', 500), DateTime.UtcNow, DateTime.UtcNow));
    }, default);
    var polished = await new MusicLibraryStore(polishRoot).ReadAsync(listener, default);
    Check(polished.Ratings.Count == 1 && polished.Ratings["al-155102851176075264"] == 4, "ratings persist; bad keys and values are dropped");
    Check(polished.Queue is { Entries.Count: MusicLibraryStore.QueueLimit, Position: 0, ChangedBy.Length: 200 } && polished.Changed > DateTime.UtcNow.AddMinutes(-1),
        "the play queue persists, bounded, with the time the library changed");
    Check(polished.Bookmarks.Single().Comment.Length == 200, "bookmarks persist with bounded text");
    Check((await keeper.ReadAsync(Guid.NewGuid(), default)).Queue is null, "a play queue belongs to one user");
    File.WriteAllText(Path.Combine(polishRoot, listener.ToString("N") + ".json"),
        """{"Ratings":{"tr-1-2":3},"Queue":{"Entries":[{"Track":{"Id":"../x","Title":"x","Artists":[],"ReleaseId":"1"},"Album":"a","At":"2026-10-30T00:00:00Z"}],"Current":"<script>","Position":5,"Changed":"2026-10-30T00:00:00Z","ChangedBy":"x"}}""");
    var tampered = await new MusicLibraryStore(polishRoot).ReadAsync(listener, default);
    Check(tampered.Ratings.Count == 0 && tampered.Queue is { Entries.Count: 0, Current: null }, "a tampered library file loses its bad ratings and queue entries");
}
finally { if (Directory.Exists(polishRoot)) Directory.Delete(polishRoot, true); }

Check(MusicArtCache.Type([0xFF, 0xD8, 0xFF, 0xE0]) == "image/jpeg" && MusicArtCache.Type([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]) == "image/png"
    && MusicArtCache.Type("RIFF0000WEBPVP8 "u8) == "image/webp" && MusicArtCache.Type("<svg onload=x>"u8) is null && MusicArtCache.Type("<html>"u8) is null,
    "cover art is recognised by its first bytes; SVG and HTML are refused");
var artRoot = Path.Combine(Path.GetTempPath(), "streamfin-art-" + Guid.NewGuid().ToString("N"));
try
{
    var art = new MusicArtCache(new SafeHttp(), Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicArtCache>.Instance, artRoot);
    Check(await art.GetAsync("https://tracks.monochrome.st/proxy/x/1.jpg", down: true, default) is null && !Directory.Exists(artRoot),
        "while the catalog is down, a cover not kept is not fetched");
}
finally { if (Directory.Exists(artRoot)) Directory.Delete(artRoot, true); }

Check(MusicRadio.NameKey("AC/DC") == MusicRadio.NameKey("acdc") && MusicRadio.NameKey("Simon & Garfunkel") == MusicRadio.NameKey("simon and garfunkel")
    && MusicRadio.NameKey("Beyoncé") != MusicRadio.NameKey("Beyonce"), "artist names compare without punctuation or case");
const string Mbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
string WithMbid(string json) => json.Replace("MBID", Mbid, StringComparison.Ordinal);
Check(MusicRadio.ParseIsrc(WithMbid("""{"recordings":[{"artist-credit":[{"artist":{"id":"MBID","name":"Radiohead"}},{"artist":{"id":"0383dadf-2a4e-4d10-a46a-e9e041da8eb3","name":"Queen"}}]},{"artist-credit":[{"artist":{"id":"MBID","name":"Radiohead"}}]}]}"""), "radiohead") == Mbid,
    "MusicBrainz ISRC lookup picks the credited artist with the catalog's name");
Check(MusicRadio.ParseIsrc("""{"recordings":[{"artist-credit":[{"artist":{"id":"../../x","name":"Radiohead"}}]}]}""", "Radiohead") is null
    && MusicRadio.ParseIsrc("{}", "Radiohead") is null && MusicRadio.ParseIsrc("[]", "Radiohead") is null, "MusicBrainz IDs must be UUIDs");
Check(MusicRadio.ParseArtistSearch(WithMbid("""{"artists":[{"id":"0383dadf-2a4e-4d10-a46a-e9e041da8eb3","score":100,"name":"Radiohead Tribute"},{"id":"MBID","score":80,"name":"Radiohead"}]}"""), "Radiohead") is null
    && MusicRadio.ParseArtistSearch(WithMbid("""{"artists":[{"id":"MBID","score":100,"name":"Radiohead"}]}"""), "Radiohead") == Mbid, "MusicBrainz name search needs a confident, same-name hit");
var similarNames = MusicRadio.ParseSimilar(WithMbid("""[{"artist_mbid":"MBID","name":"Radiohead","score":99999},{"artist_mbid":"5b11f4ce-a62d-471e-81fc-a69a8278c7da","name":"Nirvana","score":10},{"artist_mbid":"9c9f1380-2516-4fc9-a3e6-f9f61941d090","name":"Muse <b>","score":20},{"artist_mbid":"bad","name":"Bad","score":30},{"artist_mbid":"9c9f1380-2516-4fc9-a3e6-f9f61941d090","name":"MUSE B","score":5}]"""), Mbid);
Check(similarNames.Select(a => a.Name).SequenceEqual(["Muse b", "Nirvana"]), "ListenBrainz similar artists are ranked, cleaned, deduplicated and exclude the artist itself");
Check(MusicRadio.ParseSimilar("""{"error":"x"}""").Count == 0, "a ListenBrainz error object means no similar artists");
Check(MusicRadio.ParseLastFm("""{"similarartists":{"artist":[{"name":"Muse","match":"0.5"},{"name":"Nirvana","match":"1"},{"name":"Odd","match":"NaN"},{"name":"Big","match":"7"}]}}""")
    .Select(a => a.Name).SequenceEqual(["Nirvana", "Muse"]), "Last.fm similar artists are ranked and bounded");
foreach (var host in new[] { "musicbrainz.org", "labs.api.listenbrainz.org", "ws.audioscrobbler.com" }) SafeHttp.Validate(new Uri($"https://{host}/x"), false);
Check(true, "radio services are known sites");
Check(MusicPaths.Part(new string('a', 79) + " b") == new string('a', 79) && MusicPaths.Part(new string('\uFFFD', 59) + "a é") == new string('\uFFFD', 59) + "a"
    && MusicPaths.Part(new string('a', 79) + ".b") == new string('a', 79), "a shortened path part never ends in a space or a dot");
using (var page = new StreamReader(typeof(Plugin).Assembly.GetManifestResourceStream("StreamFin.Configuration.html")!))
    Check(!page.ReadToEnd().Contains("${", StringComparison.Ordinal), "the settings page has no dollar-brace text, which Jellyfin would replace with a translation");
foreach (var (text, part) in new[] { (".. .", "_"), (". .", "_"), ("..", "_"), (".", "_"), ("  ", "_"), ("a/b\\c", "a_b_c"), ("../../etc", ".._.._etc"), ("Name.", "Name"), ("AC/DC", "AC_DC"), ("x\0y", "x_y") })
    Check(MusicPaths.Part(text) == part, "music path part " + text.Replace("\0", "NUL"));
Check(MusicPaths.Part(new string('é', 200)).Length == 80 && System.Text.Encoding.UTF8.GetByteCount(MusicPaths.Part(string.Concat(Enumerable.Repeat("😀", 100)))) <= 180
    && !char.IsHighSurrogate(MusicPaths.Part(string.Concat(Enumerable.Repeat("😀", 100)))[^1]), "music path parts stay short in characters and bytes without splitting a character");
Check(MusicPaths.Root("") is null && MusicPaths.Root("relative/music") is null && MusicPaths.Root("/srv/music/") == "/srv/music" && MusicPaths.Root("/srv/x/../music") == "/srv/music",
    "the music library folder must be absolute and is normalised");
var savedAlbum = new MusicRelease("155102851176075264", "OK Computer", [new("155102851176075265", "Radiohead")], "ALBUM", new DateTime(1997, 7, 1), null, null, 12, []);
var albumFolder = MusicPaths.Album("/srv/music", savedAlbum);
Check(albumFolder == "/srv/music/Radiohead/OK Computer (1997)", "saved albums go under artist and album with the year");
Check(MusicPaths.Album("/srv/music", savedAlbum, edition: true) == "/srv/music/Radiohead/OK Computer (1997) [075264]", "a second edition of an album gets its own folder");
Check(MusicPaths.Album("/srv/music", savedAlbum with { Title = "..", Artists = [new("155102851176075265", "..")] }) == "/srv/music/_/.. (1997)" && MusicPaths.Album("/srv/music", savedAlbum with { Title = "..", Date = null }) == "/srv/music/Radiohead/_"
    && MusicPaths.Album("/srv/music", savedAlbum with { Date = null, Artists = [] }) == "/srv/music/Unknown artist/OK Computer", "album folders cannot climb out of the library folder");
var savedTrack = musicSearch.Tracks[0] with { Title = "Paranoid Android", Number = 2, Disc = 1 };
Check(MusicPaths.Track(albumFolder, savedTrack, false) == albumFolder + "/02 Paranoid Android.flac"
    && MusicPaths.Track(albumFolder, savedTrack with { Disc = 2, Title = "../x" }, true) == albumFolder + "/2-02 .._x.flac", "saved songs are numbered, with the disc when there are several");

if (args.Contains("--provider-live"))
{
    using var http = new SafeHttp();
    var heaven = new AnimeHeavenSource();
    var results = await heaven.SearchAsync("Cowboy Bebop", default);
    var series = results.Single(t => t.Title == "Cowboy Bebop");
    Console.WriteLine("Live matched title: " + series.Title + " / " + series.Id);
    var listed = await heaven.EpisodesAsync(series.Id, ordinary, default);
    Check(listed.Count == 26 && listed[0].Number == 1, "live AnimeHeaven title and episode listing");
    var gate = await AnimeHeavenPages.TextAsync(new Uri("https://animeheaven.me/gate.php"), default, headers: new Dictionary<string, string> { ["Cookie"] = "key=" + listed[0].Id, ["Referer"] = "https://animeheaven.me/anime.php" });
    foreach (System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex.Matches(gate, "<source[^>]+>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    {
        var link = System.Text.RegularExpressions.Regex.Match(tag.Value, @"src=['""]([^'""]+)['""]").Groups[1].Value;
        if (Uri.TryCreate(link, UriKind.Absolute, out var parsed)) Console.WriteLine("Observed media destination: " + parsed.GetLeftPart(UriPartial.Path));
    }
    var video = AnimeHeavenSource.ParseVideos(gate)[0];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var response = await http.OpenAsync(video.Url, true, timeout.Token, range: "bytes=0-1023", headers: video.Headers);
    Check(response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentType?.MediaType == "video/mp4", "live AnimeHeaven range request through SafeHttp");
    var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
    Check(bytes.Length == 1024, "live AnimeHeaven bounded video read");
    using var music = new MusicCatalog(http);
    var found = await music.SearchAsync("daft punk one more time", default);
    var hit = found.Tracks.First(t => t.Title == "One More Time");
    var album = await music.ReleaseAsync(hit.ReleaseId, default);
    Check(album.Tracks.Any(t => t.Id == hit.Id) && album.Tracks.Count > 5, "live music search and release");
    Check((await music.ArtistAsync(hit.Artists[0].Id, default)).Releases.Count > 5, "live music artist page");
    using var flacTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var flac = await http.OpenAsync(MusicCatalog.StreamUri(hit.Id), true, flacTimeout.Token, range: "bytes=0-65535");
    var head = await flac.Content.ReadAsByteArrayAsync(flacTimeout.Token);
    Check(flac.StatusCode == HttpStatusCode.PartialContent && head.Length == 65536 && head[..4].SequenceEqual("fLaC"u8.ToArray()), "live music stream is FLAC with byte ranges");
    using var radio = new MusicRadio(http, music);
    var daft = await music.ArtistAsync(hit.Artists[0].Id, default);
    Check(await radio.MbidAsync(daft, default) == "056e4f3e-d505-4dad-8ec1-d04f521cbb56", "live MusicBrainz ID from an ISRC");
    var mix = await radio.RadioAsync(daft.Id, 30, default);
    Check(mix.Count >= 10 && mix.SelectMany(t => t.Artists.Take(1)).Select(a => a.Id).Distinct().Count() >= 3, "live radio mixes the artist with similar ones from the catalog");
}
if (args.Contains("--hls-live"))
{
    using var http = new SafeHttp();
    async Task<int> FirstSegmentAsync(ResolvedVideo video, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var hlsLinks = new HlsLinks();
        var text = await http.TextAsync(video.Url, timeout.Token, headers: video.Headers, open: true);
        var variantUrl = video.Url;
        if (Hls.IsMaster(text))
        {
            var rewritten = Hls.RewriteMaster(text, video.Url, hlsLinks);
            variantUrl = hlsLinks.Get(Hls.FirstVariant(rewritten))!;
            Console.WriteLine($"{label}: master ok, best variant {variantUrl.Host}");
        }
        var mediaText = await http.TextAsync(variantUrl, timeout.Token, headers: video.Headers, open: true);
        var relayed = Hls.RewriteMedia(mediaText, variantUrl, hlsLinks);
        foreach (var line in relayed.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Take(5))
        {
            var segment = hlsLinks.Get(int.Parse(line[..line.IndexOf('.')]))!;
            using var response = await http.OpenAsync(segment, true, timeout.Token, headers: video.Headers, open: true);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[188];
            bytes = bytes[..await body.ReadAtLeastAsync(bytes, bytes.Length, false, timeout.Token)];
            Console.WriteLine($"{label}: {Hls.Duration(mediaText):0}s, segment {(int)response.StatusCode} from {segment.Host}, {bytes.Length} bytes, subtitles {video.Subtitles?.Count ?? 0}");
            if (response.IsSuccessStatusCode && bytes.Length > 0) return bytes[0];
        }
        return -1;
    }
    var zokoLive = await new HiAnimeSource(http).ResolveAsync("52991-1", default);
    Check(await FirstSegmentAsync(zokoLive[0], "HiAnime Frieren 1") == 0x47 && zokoLive[0].Subtitles![0].Language == "en", "live HiAnime playlist, segment and English subtitles");
    using var flix = new FlixSource(http);
    var obesity = new Film(1290938, FilmKind.Movie, "South Park: The End of Obesity", 2024, "", null, null, false);
    var movieLive = await flix.ResolveAsync(obesity, null, null, default);
    Check(!movieLive[0].Hls || await FirstSegmentAsync(movieLive[0], "FlixHQ movie") >= 0, "live FlixHQ movie resolves");
    var showLive = await flix.ResolveAsync(new Film(2190, FilmKind.Tv, "South Park", 1997, "", null, null, false), 1, 1, default);
    Check(!showLive[0].Hls || await FirstSegmentAsync(showLive[0], "FlixHQ South Park S01E01") >= 0, "live FlixHQ episode resolves");
}
Console.WriteLine($"{count} checks passed.");

sealed class FakeSource(Func<string, CancellationToken, Task<IReadOnlyList<SourceTitle>>> search) : IAnimeSource
{
    public string Id => "fake";
    public string Name => "Fake";
    public Task<IReadOnlyList<SourceTitle>> SearchAsync(string title, CancellationToken ct) => search(title, ct);
    public Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string id, Anime anime, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string id, CancellationToken ct) => throw new NotSupportedException();
}
