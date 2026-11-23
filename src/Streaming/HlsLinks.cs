using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

// Upstream addresses never reach FFmpeg. Each one gets a small index in the ticket,
// and FFmpeg only ever sees loopback paths built from those indexes.
public sealed class HlsLinks
{
    public const int Limit = 20000;
    private readonly List<(Uri Url, bool Playlist)> links = [];
    private readonly Dictionary<Uri, int> known = [];
    private readonly Dictionary<string, int> slots = new(StringComparer.Ordinal);

    // A slot names a place in a playlist ("segment 12 of p/1"). Some hosts sign every address afresh each time their
    // playlist is fetched, and FFmpeg fetches it several times; the place keeps its index and gets the newest address,
    // so the table does not fill up with stale copies of the same segments.
    public int Add(Uri url, bool playlist, string? slot = null)
    {
        SafeHttp.ValidateOpen(url);
        if (url.OriginalString.Length > 4096) throw new ProviderException("The stream playlist has an invalid address.");
        lock (links)
        {
            if (slot is not null && slots.TryGetValue(slot, out var held))
            {
                if (links[held].Url != url)
                {
                    // The stale address is forgotten, so a host that re-signs every second cannot grow the table.
                    if (known.TryGetValue(links[held].Url, out var old) && old == held) known.Remove(links[held].Url);
                    links[held] = (url, playlist);
                    known[url] = held;
                }
                return held;
            }
            if (slot is null && known.TryGetValue(url, out var existing)) return existing;
            if (links.Count >= Limit) throw new ProviderException("The stream playlist is too large.");
            links.Add((url, playlist));
            var index = links.Count - 1;
            known.TryAdd(url, index);
            if (slot is not null) slots[slot] = index;
            return index;
        }
    }
    public int Count { get { lock (links) return links.Count; } }
    public int Known { get { lock (links) return known.Count; } }
    public Uri? Get(int index) { lock (links) return index >= 0 && index < links.Count ? links[index].Url : null; }
    public bool IsPlaylist(int index) { lock (links) return links[index].Playlist; }
}
