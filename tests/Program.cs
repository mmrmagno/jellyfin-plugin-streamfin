using System.Net;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using StreamFin;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }
void Reject(Action action, string name) { try { action(); } catch (ProviderException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
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
Check(MusicCache.Key(new Uri("https://tracks.monochrome.st/track/155102871061270528")) == "155102871061270528"
    && MusicCache.Key(new Uri("http://tracks.monochrome.st/track/155102871061270528")) is null
    && MusicCache.Key(new Uri("https://evil.example/track/155102871061270528")) is null
    && MusicCache.Key(new Uri("https://tracks.monochrome.st/track/../../etc/passwd")) is null
    && MusicCache.Key(new Uri("https://tracks.monochrome.st/track/155102871061270528/x")) is null
    && MusicCache.Key(new Uri("https://tracks.monochrome.st/releases/155102871061270528")) is null, "the song cache only keeps catalog tracks, named by a valid id");
var tagged = Flac("REPLAYGAIN_TRACK_GAIN=-2.5");
Check(MusicGain.AudioStart([.. tagged, 0xFF, 0xF8]) == tagged.Length && MusicGain.AudioStart(tagged[..^1]) is null && MusicGain.AudioStart("ID3x0000"u8) is null,
    "FLAC audio starts after the last metadata block; cut short or not FLAC gives none");
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
}
finally { if (Directory.Exists(musicStoreRoot)) Directory.Delete(musicStoreRoot, true); }

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
