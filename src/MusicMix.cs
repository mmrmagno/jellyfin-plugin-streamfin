using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace StreamFin;

// Instant Mix on a StreamFin artist, album or song plays the artist's radio. Jellyfin's own mix only knows the local
// library, and its artist pages show albums only, so the Radio folder is unreachable there. The songs come from
// listing that folder through the channel manager, which makes them real items any client can play.
public sealed class MusicMix(ILibraryManager library, IChannelManager channels, IDtoService dtos, MusicCatalog catalog, MusicRadio radio)
{
    private Guid ChannelId => library.GetNewItemId("Channel " + MusicChannel.ChannelName, typeof(Channel));

    public async Task<QueryResult<BaseItemDto>?> MixAsync(BaseItem item, User user, int limit, CancellationToken ct)
    {
        if (!MusicChannel.IsAvailable || item.ChannelId != ChannelId || library.GetItemById(ChannelId) is not Channel channel) return null;
        var artist = item.ExternalId?.Split('/') switch
        {
            ["artist", var id] when MusicCatalog.IsId(id) => new MusicArtistRef(id, item.Name),
            ["album", _, var release] when MusicCatalog.IsId(release) => (await catalog.ReleaseAsync(release, ct)).Artists.FirstOrDefault(),
            ["track", _, var release, var track] when MusicCatalog.IsId(release) && MusicCatalog.IsId(track)
                => (await catalog.TrackAsync(release, track, ct)).Artists.FirstOrDefault(),
            _ => null
        };
        if (artist is null) return null;
        // The lock below is shared by every channel listing on the server, so the slow catalog and similarity
        // lookups happen first; the listings inside then answer from the caches.
        var page = await catalog.ArtistAsync(artist.Id, ct);
        await radio.RadioAsync(artist.Id, limit, ct);
        if (MusicCatalog.PreferExplicit) await catalog.WithoutCleanCopiesAsync(page.Releases, ct);
        IReadOnlyList<BaseItem> songs;
        if (!await ChannelNavigation.Gate.WaitAsync(TimeSpan.FromSeconds(20), ct)) return Empty();
        try
        {
            // An album or song found by search has no artist folder yet; it joins Search results, as a search for it would.
            var folder = library.GetItemById(library.GetNewItemId($"artist/{artist.Id}{MusicChannel.ChannelName}16", typeof(MusicArtist))) as Folder
                ?? await MusicSearch.SaveAsync<MusicArtist>(library, MusicSearch.Folder(library, channel), MusicChannel.Artist(page), _ => { }, ct);
            var children = (await channels.GetChannelItemsInternal(new InternalItemsQuery(user) { Parent = folder, ChannelIds = [ChannelId] }, new Progress<double>(), ct)).Items;
            async Task<BaseItem[]> SongsAsync(string id) => children.FirstOrDefault(c => c.ExternalId == id) is Folder mix
                ? (await channels.GetChannelItemsInternal(new InternalItemsQuery(user) { Parent = mix, ChannelIds = [ChannelId] }, new Progress<double>(), ct)).Items.Where(s => s is Audio).ToArray()
                : [];
            // Without similar artists, the artist's own top tracks, as Subsonic apps get.
            var mixed = await SongsAsync($"radio/{artist.Id}");
            if (mixed.Length == 0) mixed = await SongsAsync($"top/{artist.Id}");
            // A mix started from a song begins with that song.
            songs = (item is Audio ? mixed.Where(s => s.ExternalId?.Split('/').LastOrDefault() != item.ExternalId?.Split('/').LastOrDefault()).Prepend(item) : mixed)
                .Take(limit).ToArray();
        }
        finally { ChannelNavigation.Gate.Release(); }
        var found = dtos.GetBaseItemDtos(songs, new DtoOptions(true), user, null, false);
        return new QueryResult<BaseItemDto> { Items = found, TotalRecordCount = found.Count };
    }

    private static QueryResult<BaseItemDto> Empty() => new() { Items = [], TotalRecordCount = 0 };
}
