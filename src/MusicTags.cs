using System.Buffers.Binary;
using System.Text;

namespace StreamFin;

// The catalog serves one file per recording, tagged with whichever album the recording first came from: the song
// saved from a single or a compilation says it belongs to another album. Music libraries group songs by these tags,
// so a saved song gets its album, album artist, numbers and date from the release it was saved from.
public static class MusicTags
{
    private const int BlockLimit = (1 << 24) - 1;

    public static IReadOnlyList<(string Key, string Value)> For(MusicRelease release, MusicTrack track)
    {
        var tags = new List<(string, string)> { ("TITLE", track.Title), ("ALBUM", release.Title) };
        tags.AddRange(release.Artists.Select(a => ("ALBUMARTIST", a.Name)));
        var disc = track.Disc ?? 1;
        if (track.Number is int number) tags.Add(("TRACKNUMBER", number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        tags.Add(("TRACKTOTAL", release.Tracks.Count(t => (t.Disc ?? 1) == disc).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        tags.Add(("DISCNUMBER", disc.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        tags.Add(("DISCTOTAL", release.Tracks.Select(t => t.Disc ?? 1).Distinct().Count().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (release.Date is { } date) tags.Add(("DATE", date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        if (release.Label is { } label) tags.Add(("LABEL", label));
        if (release.Type == "COMPILATION") tags.Add(("COMPILATION", "1"));
        return tags;
    }

    // The new metadata section, from "fLaC" up to where the audio starts; null when the bytes hold no complete FLAC
    // metadata. Every block other than the Vorbis comment is kept as it was.
    public static byte[]? Retag(ReadOnlySpan<byte> flac, IReadOnlyList<(string Key, string Value)> tags)
    {
        if (MusicGain.AudioStart(flac) is null) return null;
        var blocks = new List<(int Type, byte[] Data)>();
        for (var at = 4; ;)
        {
            var header = flac[at];
            var length = (flac[at + 1] << 16) | (flac[at + 2] << 8) | flac[at + 3];
            blocks.Add((header & 0x7f, flac.Slice(at + 4, length).ToArray()));
            if (blocks.Count > 1000) return null;
            at += 4 + length;
            if ((header & 0x80) != 0) break;
        }
        if (blocks[0].Type != 0 || blocks.Count(b => b.Type == 0) != 1 || blocks.Any(b => b.Type == 127)) return null;
        var (vendor, comments) = blocks.FirstOrDefault(b => b.Type == 4) is { Data: not null } existing ? Read(existing.Data) : ("", []);
        var replaced = tags.Select(t => t.Key.ToUpperInvariant()).ToHashSet();
        var album = Value(comments, "ALBUM");
        // An album gain measured on another album is wrong for this one; players fall back to the track gain.
        var otherAlbum = album is not null && tags.FirstOrDefault(t => t.Key == "ALBUM").Value is { } title && !string.Equals(album, title, StringComparison.OrdinalIgnoreCase);
        comments = comments.Where(c => !replaced.Contains(Key(c)) && !(otherAlbum && Key(c) is "REPLAYGAIN_ALBUM_GAIN" or "REPLAYGAIN_ALBUM_PEAK")).ToList();
        comments.AddRange(tags.Where(t => t.Value.Length > 0).Select(t => t.Key.ToUpperInvariant() + "=" + Clean(t.Value)));
        var block = Write(vendor, comments);
        if (block.Length > BlockLimit) return null;
        blocks = [blocks[0], (4, block), .. blocks.Skip(1).Where(b => b.Type != 4)];
        using var output = new MemoryStream();
        output.Write("fLaC"u8);
        for (var i = 0; i < blocks.Count; i++)
        {
            var (type, data) = blocks[i];
            output.WriteByte((byte)(type | (i == blocks.Count - 1 ? 0x80 : 0)));
            output.WriteByte((byte)(data.Length >> 16));
            output.WriteByte((byte)(data.Length >> 8));
            output.WriteByte((byte)data.Length);
            output.Write(data);
        }
        return output.ToArray();
    }

    private static string Clean(string value) => new(value.Where(c => c >= ' ').ToArray());
    private static string Key(string comment) => (comment.IndexOf('=', StringComparison.Ordinal) is var split and > 0 ? comment[..split] : comment).ToUpperInvariant();
    private static string? Value(List<string> comments, string key) => comments.FirstOrDefault(c => Key(c) == key) is { } c ? c[(c.IndexOf('=', StringComparison.Ordinal) + 1)..] : null;

    // A comment block that does not parse is dropped and written new rather than trusted.
    private static (string Vendor, List<string> Comments) Read(byte[] block)
    {
        var comments = new List<string>();
        if (block.Length < 8) return ("", comments);
        var vendor = BinaryPrimitives.ReadUInt32LittleEndian(block);
        if (vendor > block.Length - 8) return ("", comments);
        var at = 4 + (int)vendor;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(at));
        at += 4;
        for (var i = 0; i < count && i < 1000 && at + 4 <= block.Length; i++)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(at));
            at += 4;
            if (size > block.Length - at) return ("", []);
            comments.Add(Encoding.UTF8.GetString(block, at, (int)size));
            at += (int)size;
        }
        return (Encoding.UTF8.GetString(block, 4, (int)vendor), comments.Where(c => c.IndexOf('=', StringComparison.Ordinal) > 0).ToList());
    }

    private static byte[] Write(string vendor, List<string> comments)
    {
        using var output = new MemoryStream();
        void Text(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)bytes.Length);
            output.Write(size);
            output.Write(bytes);
        }
        Text(vendor);
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)comments.Count);
        output.Write(count);
        foreach (var comment in comments) Text(comment);
        return output.ToArray();
    }
}
