using System.Globalization;

namespace ShadeNet.Core.Hls;

/// <summary>One quality of a stream, from <c>#EXT-X-STREAM-INF</c>.</summary>
public sealed record HlsVariant(Uri Url, long Bandwidth, int Width, int Height, string? Codecs, string? AudioGroup, double FrameRate,
    string? SubtitleGroup = null)
{
    /// <summary>"1080p · 4.5 Mbit/s", or just the bit rate when the stream does not say its size.</summary>
    public string Label
    {
        get
        {
            var rate = Bandwidth >= 1_000_000 ? $"{Bandwidth / 1_000_000.0:0.#} Mbit/s" : $"{Bandwidth / 1000} kbit/s";
            return Height > 0 ? $"{Height}p · {rate}" : rate;
        }
    }
}

/// <summary>An alternative track from <c>#EXT-X-MEDIA</c> (usually another audio language).</summary>
public sealed record HlsRendition(string Type, string GroupId, string Name, string? Language, Uri? Url, bool IsDefault, bool AutoSelect);

public enum HlsEncryption
{
    None,
    Aes128,

    /// <summary>SAMPLE-AES, Widevine, FairPlay, ...: not something a downloader can undo.</summary>
    Unsupported,
}

/// <param name="Iv">null: the IV is the segment's media sequence number (RFC 8216 §5.2).</param>
public sealed record HlsKey(HlsEncryption Method, Uri? Url, byte[]? Iv, string? Description = null);

/// <summary>The initialisation section of fragmented MP4 (<c>#EXT-X-MAP</c>).</summary>
public sealed record HlsMap(Uri Url, long? Length, long? Offset);

public sealed class HlsSegment
{
    /// <summary>Position in the playlist, from 0.</summary>
    public int Index { get; init; }

    /// <summary>The media sequence number (used for the default IV).</summary>
    public long Sequence { get; init; }

    public required Uri Url { get; init; }
    public double Duration { get; init; }
    public long? ByteLength { get; init; }
    public long? ByteOffset { get; init; }
    public HlsKey? Key { get; init; }
    public HlsMap? Map { get; init; }
    public bool Discontinuity { get; init; }

    /// <summary>The segment is a whole file that can be fetched in byte ranges (a DASH on-demand representation).</summary>
    public bool IsWholeFile { get; init; }
}

public abstract class HlsPlaylist
{
    public required Uri Url { get; init; }
}

public sealed class HlsMasterPlaylist : HlsPlaylist
{
    public List<HlsVariant> Variants { get; } = new();
    public List<HlsRendition> Renditions { get; } = new();
}

public sealed class HlsMediaPlaylist : HlsPlaylist
{
    public List<HlsSegment> Segments { get; } = new();
    public double TargetDuration { get; set; }
    public long MediaSequence { get; set; }
    public bool IsEnded { get; set; }
    public string? PlaylistType { get; set; }

    /// <summary>No end marker and not marked VOD: the playlist keeps growing (a live stream).</summary>
    public bool IsLive => !IsEnded && !string.Equals(PlaylistType, "VOD", StringComparison.OrdinalIgnoreCase);

    public double TotalDuration => Segments.Sum(s => s.Duration);

    /// <summary>The worst protection used by any segment.</summary>
    public HlsEncryption Encryption =>
        Segments.Select(s => s.Key?.Method ?? HlsEncryption.None).DefaultIfEmpty(HlsEncryption.None).Max();

    public bool UsesInitSection => Segments.Any(s => s.Map != null);
}

public class HlsFormatException : Exception
{
    public HlsFormatException(string message) : base(message) { }
}

/// <summary>Reads HLS playlists (RFC 8216). Pure: no network, no clock, so it can be tested with strings.</summary>
public static class HlsParser
{
    /// <summary>Every playlist starts with <c>#EXTM3U</c>; this is how a download recognises one that came with a wrong type.</summary>
    public static bool LooksLikePlaylist(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var span = text.AsSpan().TrimStart('﻿').TrimStart();
        return span.StartsWith("#EXTM3U", StringComparison.Ordinal);
    }

    public static HlsPlaylist Parse(string text, Uri baseUri)
    {
        if (!LooksLikePlaylist(text)) throw new HlsFormatException("This is not an HLS playlist (it does not start with #EXTM3U).");

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.Trim().TrimStart('﻿'))
            .Where(l => l.Length > 0)
            .ToList();

        return lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            ? ParseMaster(lines, baseUri)
            : ParseMedia(lines, baseUri);
    }

    // ---- master ----

    static HlsMasterPlaylist ParseMaster(List<string> lines, Uri baseUri)
    {
        var master = new HlsMasterPlaylist { Url = baseUri };
        Dictionary<string, string>? pending = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pending = Attributes(line["#EXT-X-STREAM-INF:".Length..]);
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var a = Attributes(line["#EXT-X-MEDIA:".Length..]);
                master.Renditions.Add(new HlsRendition(
                    a.GetValueOrDefault("TYPE", ""), a.GetValueOrDefault("GROUP-ID", ""), a.GetValueOrDefault("NAME", ""),
                    a.GetValueOrDefault("LANGUAGE"), Resolve(baseUri, a.GetValueOrDefault("URI")),
                    IsYes(a, "DEFAULT"), IsYes(a, "AUTOSELECT")));
            }
            else if (!line.StartsWith('#') && pending != null)
            {
                var (w, h) = Resolution(pending.GetValueOrDefault("RESOLUTION"));
                var bandwidth = ParseLong(pending.GetValueOrDefault("AVERAGE-BANDWIDTH")) is > 0 and var avg
                    ? avg
                    : ParseLong(pending.GetValueOrDefault("BANDWIDTH"));
                master.Variants.Add(new HlsVariant(Resolve(baseUri, line)!, bandwidth, w, h, pending.GetValueOrDefault("CODECS"),
                    pending.GetValueOrDefault("AUDIO"),
                    double.TryParse(pending.GetValueOrDefault("FRAME-RATE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ? fps : 0,
                    pending.GetValueOrDefault("SUBTITLES")));
                pending = null;
            }
        }

        if (master.Variants.Count == 0) throw new HlsFormatException("The playlist lists no streams.");
        return master;
    }

    // ---- media ----

    static HlsMediaPlaylist ParseMedia(List<string> lines, Uri baseUri)
    {
        var media = new HlsMediaPlaylist { Url = baseUri };
        HlsKey? key = null;
        HlsMap? map = null;
        double? duration = null;
        long? rangeLength = null, rangeOffset = null;
        var discontinuity = false;
        var nextOffsets = new Dictionary<string, long>();   // where the next byte range of a resource begins
        long sequence = 0;
        var sequenceSet = false;

        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                {
                    media.TargetDuration = ParseDouble(line["#EXT-X-TARGETDURATION:".Length..]);
                }
                else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                {
                    media.MediaSequence = ParseLong(line["#EXT-X-MEDIA-SEQUENCE:".Length..]);
                    if (!sequenceSet) sequence = media.MediaSequence;
                    sequenceSet = true;
                }
                else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal))
                {
                    media.PlaylistType = line["#EXT-X-PLAYLIST-TYPE:".Length..].Trim();
                }
                else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
                {
                    media.IsEnded = true;
                }
                else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    var value = line["#EXTINF:".Length..];
                    var comma = value.IndexOf(',');
                    duration = ParseDouble(comma >= 0 ? value[..comma] : value);
                }
                else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                {
                    var value = line["#EXT-X-BYTERANGE:".Length..];
                    var at = value.IndexOf('@');
                    rangeLength = ParseLong(at >= 0 ? value[..at] : value);
                    rangeOffset = at >= 0 ? ParseLong(value[(at + 1)..]) : null;
                }
                else if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal) && !line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal))
                {
                    discontinuity = true;
                }
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                {
                    key = ParseKey(Attributes(line["#EXT-X-KEY:".Length..]), baseUri);
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                {
                    var a = Attributes(line["#EXT-X-MAP:".Length..]);
                    if (Resolve(baseUri, a.GetValueOrDefault("URI")) is { } mapUrl)
                    {
                        long? length = null, offset = null;
                        if (a.GetValueOrDefault("BYTERANGE") is { } br)
                        {
                            var at = br.IndexOf('@');
                            length = ParseLong(at >= 0 ? br[..at] : br);
                            offset = at >= 0 ? ParseLong(br[(at + 1)..]) : 0;
                        }

                        map = new HlsMap(mapUrl, length, offset);
                    }
                }

                continue;
            }

            if (duration == null) continue;   // a URI line that no #EXTINF announced is not a segment

            var url = Resolve(baseUri, line)!;
            long? start = null;
            if (rangeLength != null)
            {
                start = rangeOffset ?? nextOffsets.GetValueOrDefault(url.AbsoluteUri, 0);
                nextOffsets[url.AbsoluteUri] = start.Value + rangeLength.Value;
            }

            media.Segments.Add(new HlsSegment
            {
                Index = media.Segments.Count,
                Sequence = sequence++,
                Url = url,
                Duration = duration.Value,
                ByteLength = rangeLength,
                ByteOffset = start,
                Key = key,
                Map = map,
                Discontinuity = discontinuity,
            });

            duration = null;
            rangeLength = rangeOffset = null;
            discontinuity = false;
        }

        if (media.Segments.Count == 0) throw new HlsFormatException("The playlist has no media segments.");
        return media;
    }

    static HlsKey ParseKey(Dictionary<string, string> a, Uri baseUri)
    {
        var method = a.GetValueOrDefault("METHOD", "NONE").ToUpperInvariant();
        var format = a.GetValueOrDefault("KEYFORMAT");

        if (method == "NONE") return new HlsKey(HlsEncryption.None, null, null);

        // Only the standard way of delivering AES-128 keys is understood; a DRM key format is something else entirely.
        var standard = string.IsNullOrEmpty(format) || format == "identity";
        if (method != "AES-128" || !standard)
            return new HlsKey(HlsEncryption.Unsupported, Resolve(baseUri, a.GetValueOrDefault("URI")), null,
                $"{method}{(standard ? "" : " / " + format)}");

        byte[]? iv = null;
        if (a.GetValueOrDefault("IV") is { Length: > 2 } ivText && ivText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = ivText[2..].PadLeft(32, '0');
            if (hex.Length == 32) iv = Convert.FromHexString(hex);
        }

        return new HlsKey(HlsEncryption.Aes128, Resolve(baseUri, a.GetValueOrDefault("URI")), iv);
    }

    // ---- helpers ----

    /// <summary>The 16-byte IV for a segment without an explicit one: its sequence number, big endian.</summary>
    public static byte[] DefaultIv(long sequence)
    {
        var iv = new byte[16];
        for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    /// <summary>Attribute lists: <c>KEY=value,KEY="quoted, value"</c>.</summary>
    public static Dictionary<string, string> Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            var eq = text.IndexOf('=', i);
            if (eq < 0) break;
            var name = text[i..eq].Trim();
            i = eq + 1;

            string value;
            if (i < text.Length && text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                if (close < 0) close = text.Length;
                value = text[(i + 1)..close];
                i = Math.Min(text.Length, close + 1);
            }
            else
            {
                var comma = text.IndexOf(',', i);
                if (comma < 0) comma = text.Length;
                value = text[i..comma].Trim();
                i = comma;
            }

            if (i < text.Length && text[i] == ',') i++;
            if (name.Length > 0) result[name] = value;
        }

        return result;
    }

    static bool IsYes(Dictionary<string, string> a, string name) =>
        a.TryGetValue(name, out var v) && string.Equals(v, "YES", StringComparison.OrdinalIgnoreCase);

    static Uri? Resolve(Uri baseUri, string? reference) =>
        string.IsNullOrWhiteSpace(reference) ? null
        : Uri.TryCreate(baseUri, reference.Trim(), out var uri) ? uri : null;

    static (int Width, int Height) Resolution(string? text)
    {
        if (text == null) return (0, 0);
        var parts = text.Split('x', 'X');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) ? (w, h) : (0, 0);
    }

    static long ParseLong(string? text) =>
        long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static double ParseDouble(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>Which quality to download when the user did not choose.</summary>
public static class HlsChoice
{
    /// <summary>The sharpest picture; the higher bit rate breaks a tie.</summary>
    public static HlsVariant Best(IEnumerable<HlsVariant> variants) =>
        variants.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).First();

    /// <summary>
    /// The variant that best matches an earlier choice (same height, then the nearest bit rate): after a refreshed address
    /// the playlist may list the same qualities under other URLs.
    /// </summary>
    public static HlsVariant Match(IReadOnlyList<HlsVariant> variants, int height, long bandwidth)
    {
        var sameHeight = variants.Where(v => v.Height == height).ToList();
        var pool = sameHeight.Count > 0 ? sameHeight : variants;
        return pool.OrderBy(v => Math.Abs(v.Bandwidth - bandwidth)).First();
    }

    /// <summary>The subtitle tracks of a variant's group that have a playlist of their own.</summary>
    public static IReadOnlyList<HlsRendition> SubtitlesFor(HlsMasterPlaylist master, HlsVariant variant) =>
        variant.SubtitleGroup == null
            ? Array.Empty<HlsRendition>()
            : master.Renditions.Where(r => r.Type.Equals("SUBTITLES", StringComparison.OrdinalIgnoreCase) && r.GroupId == variant.SubtitleGroup && r.Url != null).ToList();

    /// <summary>The audio track to add for a variant: its group's default (or first) rendition that has its own playlist.</summary>
    public static HlsRendition? AudioFor(HlsMasterPlaylist master, HlsVariant variant, string? preferredName = null)
    {
        if (variant.AudioGroup == null) return null;
        var candidates = master.Renditions
            .Where(r => r.Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase) && r.GroupId == variant.AudioGroup && r.Url != null)
            .ToList();
        if (candidates.Count == 0) return null;
        return candidates.FirstOrDefault(r => preferredName != null && r.Name == preferredName)
               ?? candidates.FirstOrDefault(r => r.IsDefault)
               ?? candidates.First();
    }
}
