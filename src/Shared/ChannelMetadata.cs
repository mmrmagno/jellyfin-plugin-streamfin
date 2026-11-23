using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

// The wide pictures a title has besides its poster. A channel listing carries one image only (Jellyfin saves it as
// Primary), so these are kept by item id when the listing is built and added to the saved item afterwards.
public sealed record Artwork(string? Backdrop = null, string? Logo = null, string? Banner = null);

public static class ArtworkStore
{
    private static readonly MemoryCache Known = new(new MemoryCacheOptions { SizeLimit = 20000 });
    public static void Set(string id, Artwork art)
    {
        if (art.Backdrop is null && art.Logo is null && art.Banner is null) return;
        Known.Set(id, art, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(6) });
    }
    public static Artwork? Get(string id) => Known.TryGetValue<Artwork>(id, out var art) ? art : null;
}

// Jellyfin copies a channel item's metadata only when it first creates the item; after that the saved item keeps what it
// had. This brings saved items up to date after each listing: rating, genres, studios, tags, age rating, premiere,
// provider ids, people and the wide pictures. Names, overviews and numbering are left to the channels' own repairs.
public static class ChannelMetadata
{
    // Adding people makes Jellyfin create person entries and fetch their photos, which takes seconds per title, so
    // saved items are brought up to date in the background, one batch at a time, never while a listing waits.
    // At most a few batches wait; quick browsing that asks for more drops them, and the next listing catches up.
    private static readonly SemaphoreSlim Running = new(1, 1);
    private static int waiting;
    public static void InBackground(Func<CancellationToken, Task> work, Microsoft.Extensions.Logging.ILogger logger)
    {
        if (Interlocked.Increment(ref waiting) > 8) { Interlocked.Decrement(ref waiting); return; }
        _ = Task.Run(async () =>
        {
            await Running.WaitAsync();
            Interlocked.Decrement(ref waiting);
            // Jellyfin is still turning the listing into its answer for a moment; the saved items wait for it.
            await Task.Delay(TimeSpan.FromSeconds(3));
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                await work(limit.Token);
            }
            catch (Exception ex) { Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "StreamFin: saved items could not be updated ({ErrorType})", ex.GetType().Name); }
            finally { Running.Release(); }
        }, CancellationToken.None);
    }

    public static async Task<int> SyncAsync(ILibraryManager library, string channel, IEnumerable<ChannelItemInfo> items, CancellationToken ct)
    {
        var updated = 0;
        foreach (var info in items)
        {
            if (string.IsNullOrEmpty(info.Id) || info.Id.EndsWith("/status", StringComparison.Ordinal)) continue;
            if (library.GetItemById(library.GetNewItemId(info.Id + channel + "16", TypeFor(info))) is not { } item) continue;
            // One title that cannot be updated does not stop the rest.
            try { if (await ApplyAsync(library, item, info, ct)) updated++; }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }
        return updated;
    }

    public static async Task<bool> ApplyAsync(ILibraryManager library, BaseItem item, ChannelItemInfo info, CancellationToken ct)
    {
        var changed = Plan(item, info, ArtworkStore.Get(info.Id));
        if (changed) await library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct);
        if (info.People is { Count: > 0 } people && PeopleDiffer(library.GetPeople(item), people))
            await library.UpdatePeopleAsync(item, people, ct);
        return changed;
    }

    // Copies onto the item what the listing knows and the item lacks or has differently; says whether anything changed.
    public static bool Plan(BaseItem item, ChannelItemInfo info, Artwork? art)
    {
        var changed = false;
        if (info.Genres is { Count: > 0 } genres && !(item.Genres ?? []).SequenceEqual(genres)) { item.Genres = [.. genres]; changed = true; }
        if (info.Studios is { Count: > 0 } studios && !(item.Studios ?? []).SequenceEqual(studios)) { item.Studios = [.. studios]; changed = true; }
        if (info.CommunityRating is { } rating && item.CommunityRating != rating) { item.CommunityRating = rating; changed = true; }
        if (info.OfficialRating is { Length: > 0 } official && item.OfficialRating != official) { item.OfficialRating = official; changed = true; }
        if (info.PremiereDate is { } premiere && item.PremiereDate != premiere) { item.PremiereDate = premiere; changed = true; }
        // Tags are added, never taken away: "Streamable" and other tags set elsewhere stay.
        var tags = (item.Tags ?? []).Union(info.Tags ?? [], StringComparer.Ordinal).ToArray();
        if (tags.Length != (item.Tags ?? []).Length) { item.Tags = tags; changed = true; }
        // A new dictionary swapped in whole: Jellyfin may be reading the old one at this moment.
        if ((info.ProviderIds ?? []).Any(pair => !item.ProviderIds.TryGetValue(pair.Key, out var current) || current != pair.Value))
        {
            item.ProviderIds = MergeIds(item.ProviderIds, info.ProviderIds!);
            changed = true;
        }
        if (art is not null)
        {
            changed |= AddImage(item, ImageType.Backdrop, art.Backdrop);
            changed |= AddImage(item, ImageType.Logo, art.Logo);
            changed |= AddImage(item, ImageType.Banner, art.Banner);
            changed |= AddImage(item, ImageType.Thumb, art.Backdrop);
        }
        return changed;
    }

    // Only a picture the item does not have yet, and only over https.
    public static bool AddImage(BaseItem item, ImageType type, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || item.HasImage(type) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        item.SetImagePath(type, url);
        return true;
    }

    // Compared by name: Jellyfin keeps one entry per person on an item.
    public static bool PeopleDiffer(IReadOnlyList<PersonInfo> current, IReadOnlyList<PersonInfo> wanted) =>
        !current.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(wanted.Select(p => p.Name));

    public static Type TypeFor(ChannelItemInfo info) => info.Type == ChannelItemType.Media
        ? info.ContentType switch
        {
            ChannelMediaContentType.Episode => typeof(Episode),
            ChannelMediaContentType.Movie => typeof(Movie),
            ChannelMediaContentType.Song => typeof(Audio),
            _ => typeof(Video)
        }
        : info.FolderType switch
        {
            ChannelFolderType.Series => typeof(Series),
            ChannelFolderType.Season => typeof(Season),
            ChannelFolderType.MusicAlbum => typeof(MusicAlbum),
            ChannelFolderType.MusicArtist => typeof(MusicArtist),
            _ => typeof(Folder)
        };

    // A new dictionary each time: Jellyfin may be reading the old one on another thread.
    public static Dictionary<string, string> MergeIds(IReadOnlyDictionary<string, string>? current, IReadOnlyDictionary<string, string> add)
    {
        var ids = new Dictionary<string, string>(current ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in add) ids[key] = value;
        return ids;
    }
}
