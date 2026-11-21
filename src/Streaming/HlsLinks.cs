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
    public int Add(Uri url, bool playlist)
    {
        SafeHttp.ValidateOpen(url);
        if (url.OriginalString.Length > 4096) throw new ProviderException("The stream playlist has an invalid address.");
        lock (links)
        {
            if (known.TryGetValue(url, out var existing)) return existing;
            if (links.Count >= Limit) throw new ProviderException("The stream playlist is too large.");
            links.Add((url, playlist));
            return known[url] = links.Count - 1;
        }
    }
    public Uri? Get(int index) { lock (links) return index >= 0 && index < links.Count ? links[index].Url : null; }
    public bool IsPlaylist(int index) { lock (links) return links[index].Playlist; }
}
