using System.Text.Json;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using StreamFin;
using static Harness;

// Ratings, genres, studios, tags, people and pictures from AniList and TMDB, and their update on saved items.
static class MetadataChecks
{
    public static void Run()
    {
        using var anilist = JsonDocument.Parse("""
            {"id":20,"idMal":20,"title":{"romaji":"Naruto","english":"Naruto","native":null},"synonyms":[],"startDate":{"year":2002},
             "description":"Ninja.","coverImage":{"large":"https://s4.anilist.co/c.jpg"},"bannerImage":"https://s4.anilist.co/b.jpg","duration":23,
             "isAdult":false,"averageScore":79,"genres":["Action"],"episodes":220,"nextAiringEpisode":null,
             "tags":[{"name":"Ninja","rank":95,"isMediaSpoiler":false},{"name":"Twist","rank":99,"isMediaSpoiler":true},{"name":"Shounen","rank":90,"isMediaSpoiler":false}],
             "studios":{"nodes":[{"name":"Pierrot","isAnimationStudio":true},{"name":"TV Tokyo","isAnimationStudio":false}]}}
            """);
        var naruto = AniListCatalog.Parse(anilist.RootElement);
        Check(ChannelMetadata.MergeIds(new Dictionary<string, string> { ["Imdb"] = "tt0137523", ["Tmdb"] = "1" }, new Dictionary<string, string> { ["tmdb"] = "550", ["Custom"] = "x" })
                is { Count: 3 } merged && merged["Imdb"] == "tt0137523" && merged["Tmdb"] == "550" && merged["Custom"] == "x",
            "a search keeps the ids a title was described with and updates its own");
        Check(naruto is { AverageScore: 79, Banner: "https://s4.anilist.co/b.jpg", MalId: 20 } && naruto.Tags!.SequenceEqual(["Ninja", "Shounen"]) && naruto.Studios!.SequenceEqual(["Pierrot"]),
            "AniList gives score, banner, the strongest non-spoiler tags and the animation studios");
        using var foreign = JsonDocument.Parse(anilist.RootElement.GetRawText().Replace("https://s4.anilist.co/b.jpg", "https://evil.example/b.jpg"));
        Check(AniListCatalog.Parse(foreign.RootElement).Banner is null, "a banner from anywhere but AniList's own images is dropped");
        var series = new ChannelItemInfo { Id = "title/popular/20", ProviderIds = new() { ["Custom"] = "anifin-series-20" } };
        AnimeChannel.Describe(naruto, series);
        Check(series.CommunityRating == 7.9f && series.Studios.SequenceEqual(["Pierrot"]) && series.Tags.SequenceEqual(["StreamFin", "Ninja", "Shounen"])
            && series.ProviderIds["AniList"] == "20" && series.ProviderIds["MyAnimeList"] == "20" && series.ProviderIds["Custom"] == "anifin-series-20"
            && ArtworkStore.Get("title/popular/20")?.Backdrop == "https://s4.anilist.co/b.jpg", "an anime series gets rating, studios, tags, ids and its banner, and keeps its progress id");

        using var listed = JsonDocument.Parse("""{"id":27205,"title":"Inception","release_date":"2010-07-15","overview":"Dreams.","poster_path":"/p.jpg","backdrop_path":"/b.jpg","vote_average":8.369,"genre_ids":[28,878,99999]}""");
        var inception = TmdbCatalog.Parse(listed.RootElement, FilmKind.Movie)!;
        Check(inception is { Premiere.Year: 2010, Backdrop: "https://image.tmdb.org/t/p/w1280/b.jpg" } && TmdbCatalog.Rating(inception) == 8.4f
            && TmdbCatalog.Genres(inception).SequenceEqual(["Action", "Science Fiction"]), "a TMDB listing gives rating, premiere, backdrop and named genres (unknown ids dropped)");
        Check(TmdbCatalog.Rating(inception with { Vote = 0 }) is null, "an unrated title has no rating rather than zero");
        using var movie = JsonDocument.Parse("""
            {"genres":[{"id":28,"name":"Action"}],"production_companies":[{"name":"Legendary"},{"name":"Syncopy"}],
             "external_ids":{"imdb_id":"tt1375666"},
             "credits":{"cast":[{"name":"Leonardo DiCaprio","character":"Cobb","profile_path":"/l.jpg"},{"name":"Elliot Page","character":"Ariadne"}],
                        "crew":[{"name":"Christopher Nolan","job":"Director","profile_path":"/n.jpg"},{"name":"Hans Zimmer","job":"Original Music Composer"}]},
             "release_dates":{"results":[{"iso_3166_1":"DE","release_dates":[{"certification":"12"}]},{"iso_3166_1":"US","release_dates":[{"certification":""},{"certification":"PG-13"}]}]},
             "backdrop_path":"/wide.jpg","images":{"logos":[{"file_path":"/de.png","iso_639_1":"de","vote_average":9},{"file_path":"/en.png","iso_639_1":"en","vote_average":5}]}}
            """);
        var details = TmdbCatalog.Details(movie.RootElement, FilmKind.Movie);
        Check(details is { ImdbId: "tt1375666", Certification: "PG-13", Logo: "https://image.tmdb.org/t/p/w500/en.png" } && details.Studios.SequenceEqual(["Legendary", "Syncopy"])
            && details.Cast is [{ Name: "Leonardo DiCaprio", Role: "Cobb", Photo: "https://image.tmdb.org/t/p/w185/l.jpg" }, { Photo: null }]
            && details.Directors is [{ Name: "Christopher Nolan" }], "movie details give IMDb id, US rating, studios, cast with roles and faces, director and the English logo");
        using var show = JsonDocument.Parse("""
            {"networks":[{"name":"AMC"}],"created_by":[{"name":"Vince Gilligan"}],"external_ids":{"imdb_id":"tt0903747"},
             "aggregate_credits":{"cast":[{"name":"Bryan Cranston","roles":[{"character":"Walter White"}]}]},
             "content_ratings":{"results":[{"iso_3166_1":"US","rating":"TV-MA"}]}}
            """);
        var bad = TmdbCatalog.Details(show.RootElement, FilmKind.Tv);
        Check(bad is { Certification: "TV-MA", Cast: [{ Role: "Walter White" }], Directors: [{ Name: "Vince Gilligan", Role: "Creator" }] } && bad.Studios.SequenceEqual(["AMC"]),
            "show details give the network, creator, first role of each actor and the TV rating");
        var item = new ChannelItemInfo { Id = "movie/popular/27205", ProviderIds = new() };
        FilmChannel.Describe(inception, details, item);
        Check(item is { CommunityRating: 8.4f, OfficialRating: "PG-13" } && item.ProviderIds["Imdb"] == "tt1375666" && item.People.Count == 3
            && item.People.Count(p => p.Type == Jellyfin.Data.Enums.PersonKind.Director) == 1 && ArtworkStore.Get(item.Id)?.Logo == "https://image.tmdb.org/t/p/w500/en.png",
            "a movie gets rating, age rating, IMDb id, people and its logo");

        var saved = new MediaBrowser.Controller.Entities.Movies.Movie { Tags = ["StreamFin", "Streamable"] };
        saved.ProviderIds["Custom"] = "streamfin-movie-27205";
        var changed = ChannelMetadata.Plan(saved, item, new Artwork(Backdrop: "https://image.tmdb.org/t/p/w1280/b.jpg", Logo: "http://insecure.example/logo.png"));
        Check(changed && saved.CommunityRating == 8.4f && saved.OfficialRating == "PG-13" && saved.Genres.SequenceEqual(["Action"]) && saved.Tags.Contains("Streamable")
            && saved.ProviderIds["Imdb"] == "tt1375666" && saved.ProviderIds["Custom"] == "streamfin-movie-27205" && saved.HasImage(ImageType.Backdrop) && !saved.HasImage(ImageType.Logo),
            "a saved movie is brought up to date in place, keeps its tags and ids, and takes https pictures only");
        Check(!ChannelMetadata.Plan(saved, item, new Artwork(Backdrop: "https://image.tmdb.org/t/p/w1280/other.jpg")), "a second pass changes nothing and keeps the first backdrop");
        Check(ChannelMetadata.TypeFor(new ChannelItemInfo { Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Season }) == typeof(MediaBrowser.Controller.Entities.TV.Season)
            && ChannelMetadata.TypeFor(new ChannelItemInfo { Type = ChannelItemType.Media, ContentType = ChannelMediaContentType.Movie }) == typeof(MediaBrowser.Controller.Entities.Movies.Movie),
            "saved items are found by the same type Jellyfin created them as");
    }
}
