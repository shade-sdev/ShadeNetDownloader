using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ShadeNet.Core.Hls;

/// <summary>One quality (or audio / subtitle track) of a DASH presentation.</summary>
public sealed class DashRepresentation
{
    public required string Id { get; init; }

    /// <summary>"video", "audio" or "text".</summary>
    public required string Kind { get; init; }

    public long Bandwidth { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Codecs { get; init; }
    public string? MimeType { get; init; }
    public string? Language { get; init; }
    public string? Label { get; init; }
    public bool IsMain { get; init; }

    /// <summary>The track's name as the user sees it: label, language or id.</summary>
    public string Name => !string.IsNullOrEmpty(Label) ? Label : !string.IsNullOrEmpty(Language) ? Language : Id;

    /// <summary>The segment list at a moment in time (a live presentation lists what is available at <paramref name="now"/>).</summary>
    public required Func<DateTimeOffset, HlsMediaPlaylist> Build { get; init; }

    /// <summary>File extension for a subtitle track.</summary>
    public string TextExtension =>
        MimeType?.Contains("vtt", StringComparison.OrdinalIgnoreCase) == true || Codecs == "wvtt" ? ".vtt"
        : MimeType?.Contains("ttml", StringComparison.OrdinalIgnoreCase) == true || Codecs == "stpp" ? ".ttml"
        : ".xml";
}

public sealed class DashManifest : HlsPlaylist
{
    public bool IsDynamic { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>How often a live presentation should be asked again (seconds).</summary>
    public double UpdateSeconds { get; init; }

    public List<DashRepresentation> Video { get; } = new();
    public List<DashRepresentation> Audio { get; } = new();
    public List<DashRepresentation> Text { get; } = new();

    /// <summary>Set when the content is protected (any <c>ContentProtection</c>): the scheme, for the message.</summary>
    public string? Protection { get; init; }

    /// <summary>The qualities as the same kind of object HLS uses, each tied to its representation by the address fragment.</summary>
    public IReadOnlyList<(HlsVariant Variant, DashRepresentation Representation)> VideoVariants() =>
        Video.Select(r => (new HlsVariant(new Uri(Url, "#" + Uri.EscapeDataString(r.Id)), r.Bandwidth, r.Width, r.Height, r.Codecs, null, 0), r)).ToList();

    /// <summary>The audio track to use: the one named, else the main one (the best quality of it).</summary>
    public DashRepresentation? PickAudio(string? name)
    {
        if (Audio.Count == 0) return null;
        var pool = name != null && Audio.Where(a => a.Name == name || a.Id == name || a.Language == name).ToList() is { Count: > 0 } named
            ? named
            : Audio.Where(a => a.IsMain).ToList() is { Count: > 0 } main ? main : Audio;
        return pool.OrderByDescending(a => a.Bandwidth).First();
    }
}

/// <summary>
/// Reads an MPEG-DASH manifest (MPD) and turns each representation into the list of segments the downloader already
/// understands (<see cref="HlsMediaPlaylist"/>): <c>SegmentTemplate</c> (number, time, timeline), <c>SegmentList</c> and a
/// plain <c>BaseURL</c> file. Pure: no network and the clock is passed in.
/// </summary>
public static class DashParser
{
    public static bool LooksLikeManifest(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var head = text.AsSpan(0, Math.Min(text.Length, 2000));
        return head.Contains("<MPD", StringComparison.Ordinal);
    }

    public static DashManifest Parse(string text, Uri baseUri)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text.TrimStart('﻿'));
        }
        catch (XmlException ex)
        {
            throw new HlsFormatException("The manifest is not valid XML: " + ex.Message);
        }

        var mpd = doc.Root;
        if (mpd == null || mpd.Name.LocalName != "MPD") throw new HlsFormatException("This is not a DASH manifest (no MPD element).");

        var dynamic = (string?)mpd.Attribute("type") == "dynamic";
        var total = Duration((string?)mpd.Attribute("mediaPresentationDuration"));
        var update = Duration((string?)mpd.Attribute("minimumUpdatePeriod"));
        var availabilityStart = DateTimeOffset.TryParse((string?)mpd.Attribute("availabilityStartTime"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var a) ? a : DateTimeOffset.UnixEpoch;
        var timeShift = Duration((string?)mpd.Attribute("timeShiftBufferDepth"));

        var mpdBase = ResolveBase(baseUri, mpd);
        string? protection = null;
        foreach (var cp in mpd.Descendants().Where(e => e.Name.LocalName == "ContentProtection"))
        {
            protection = (string?)cp.Attribute("schemeIdUri") ?? "content protection";
            break;
        }

        var manifest = new DashManifest
        {
            Url = baseUri,
            IsDynamic = dynamic,
            DurationSeconds = total,
            UpdateSeconds = update > 0 ? update : 2,
            Protection = protection,
        };

        var periods = mpd.Elements().Where(e => e.Name.LocalName == "Period").ToList();
        if (periods.Count == 0) throw new HlsFormatException("The manifest has no Period.");

        // each representation collects its pieces from every period; the periods follow each other
        var byKey = new Dictionary<string, List<Func<DateTimeOffset, int, List<HlsSegment>>>>();
        var infos = new Dictionary<string, (string Kind, XElement Rep, XElement Set)>();
        var order = new List<string>();

        var periodStart = 0.0;
        for (var pi = 0; pi < periods.Count; pi++)
        {
            var period = periods[pi];
            var start = Duration((string?)period.Attribute("start"), periodStart);
            var next = pi + 1 < periods.Count ? Duration((string?)periods[pi + 1].Attribute("start"), -1) : -1;
            var length = Duration((string?)period.Attribute("duration"), -1);
            if (length < 0 && next >= 0) length = next - start;
            if (length < 0 && total > 0) length = total - start;
            periodStart = start + Math.Max(length, 0);

            var periodBase = ResolveBase(mpdBase, period);
            foreach (var set in period.Elements().Where(e => e.Name.LocalName == "AdaptationSet"))
            {
                var setBase = ResolveBase(periodBase, set);
                foreach (var rep in set.Elements().Where(e => e.Name.LocalName == "Representation"))
                {
                    var kind = KindOf(set, rep);
                    if (kind == null) continue;

                    var repBase = ResolveBase(setBase, rep);
                    var id = (string?)rep.Attribute("id") ?? $"{kind}{order.Count}";
                    var key = kind + "|" + id + "|" + ((string?)set.Attribute("lang") ?? "");
                    if (!byKey.TryGetValue(key, out var pieces))
                    {
                        byKey[key] = pieces = new();
                        infos[key] = (kind, rep, set);
                        order.Add(key);
                    }

                    var ctx = new PeriodContext(period, set, rep, repBase, id, start, length, dynamic, availabilityStart, timeShift, total);
                    pieces.Add((now, firstIndex) => Segments(ctx, now, firstIndex));
                }
            }
        }

        foreach (var key in order)
        {
            var (kind, rep, set) = infos[key];
            var pieces = byKey[key];
            var id = (string?)rep.Attribute("id") ?? key;
            var width = Int(Attr("width", rep, set));
            var height = Int(Attr("height", rep, set));
            var role = set.Elements().FirstOrDefault(e => e.Name.LocalName == "Role")?.Attribute("value")?.Value;

            var representation = new DashRepresentation
            {
                Id = id,
                Kind = kind,
                Bandwidth = Int(Attr("bandwidth", rep, set)),
                Width = width,
                Height = height,
                Codecs = Attr("codecs", rep, set),
                MimeType = Attr("mimeType", rep, set),
                Language = (string?)set.Attribute("lang"),
                Label = set.Elements().FirstOrDefault(e => e.Name.LocalName == "Label")?.Value,
                IsMain = role == "main",
                Build = now =>
                {
                    var playlist = new HlsMediaPlaylist
                    {
                        Url = baseUri,
                        IsEnded = !dynamic,
                        PlaylistType = dynamic ? null : "VOD",
                    };
                    foreach (var piece in pieces) playlist.Segments.AddRange(piece(now, playlist.Segments.Count));
                    playlist.TargetDuration = playlist.Segments.Count > 0 ? playlist.Segments.Max(s => s.Duration) : 0;
                    return playlist;
                },
            };

            (kind switch { "video" => manifest.Video, "audio" => manifest.Audio, _ => manifest.Text }).Add(representation);
        }

        if (manifest.Video.Count == 0 && manifest.Audio.Count == 0) throw new HlsFormatException("The manifest lists no video or audio.");
        return manifest;
    }

    // ---- segments ----

    sealed record PeriodContext(XElement Period, XElement Set, XElement Rep, Uri Base, string Id, double Start, double Length,
        bool Dynamic, DateTimeOffset AvailabilityStart, double TimeShift, double Total);

    static List<HlsSegment> Segments(PeriodContext c, DateTimeOffset now, int firstIndex)
    {
        var result = new List<HlsSegment>();
        var bandwidth = Int(Attr("bandwidth", c.Rep, c.Set));

        // templates inherit attribute by attribute: Representation, then AdaptationSet, then Period
        var templates = new[] { c.Rep, c.Set, c.Period }
            .Select(e => e.Elements().FirstOrDefault(x => x.Name.LocalName == "SegmentTemplate")).Where(e => e != null).Cast<XElement>().ToList();
        var lists = new[] { c.Rep, c.Set, c.Period }
            .Select(e => e.Elements().FirstOrDefault(x => x.Name.LocalName == "SegmentList")).Where(e => e != null).Cast<XElement>().ToList();

        if (templates.Count > 0)
        {
            string? T(string name) => templates.Select(t => (string?)t.Attribute(name)).FirstOrDefault(v => v != null);
            var media = T("media");
            if (media == null) return result;
            var timescale = Math.Max(1, Long(T("timescale"), 1));
            var startNumber = Long(T("startNumber"), 1);
            var duration = Long(T("duration"), 0);
            var timeline = templates.Select(t => t.Elements().FirstOrDefault(x => x.Name.LocalName == "SegmentTimeline")).FirstOrDefault(x => x != null);

            HlsMap? map = null;
            if (T("initialization") is { } init) map = new HlsMap(Absolute(c.Base, Expand(init, c.Id, bandwidth, 0, 0)), null, null);

            if (timeline != null)
            {
                long time = 0;
                var number = startNumber;
                var first = true;
                foreach (var s in timeline.Elements().Where(x => x.Name.LocalName == "S"))
                {
                    var d = Long((string?)s.Attribute("d"), 0);
                    if (d <= 0) continue;
                    if ((string?)s.Attribute("t") is { } t0) time = Long(t0, time);
                    else if (first) time = Long(T("presentationTimeOffset"), 0);
                    first = false;

                    var repeat = (int)Long((string?)s.Attribute("r"), 0);
                    if (repeat < 0)
                    {
                        // "until the end of the period"
                        var end = (c.Length > 0 ? c.Length : c.Total - c.Start) * timescale;
                        repeat = end > time ? (int)Math.Max(0, Math.Ceiling((end - time) / (double)d) - 1) : 0;
                    }

                    for (var i = 0; i <= repeat; i++)
                    {
                        result.Add(Make(c, firstIndex + result.Count, c.Dynamic ? time : firstIndex + result.Count, media, c.Id, bandwidth, number, time, d / (double)timescale, map));
                        time += d;
                        number++;
                    }
                }

                return result;
            }

            if (duration <= 0) return result;
            var seconds = duration / (double)timescale;
            long count;
            long skip = 0;

            if (c.Dynamic)
            {
                // what a live presentation offers right now: the segments inside the time-shift window, up to the newest complete one
                var elapsed = (now - c.AvailabilityStart).TotalSeconds - c.Start;
                var newest = (long)Math.Floor(elapsed / seconds) - 1;
                if (newest < 0) return result;
                var window = c.TimeShift > 0 ? c.TimeShift : 60;
                var oldest = Math.Max(0, (long)Math.Floor((elapsed - window) / seconds));
                oldest = Math.Max(oldest, newest - 29);
                skip = oldest;
                count = newest + 1;
            }
            else
            {
                var length = c.Length > 0 ? c.Length : c.Total - c.Start;
                if (length <= 0) return result;
                count = (long)Math.Ceiling(length / seconds);
            }

            for (var k = skip; k < count; k++)
            {
                var number = startNumber + k;
                var time = k * duration;
                var piece = seconds;
                if (!c.Dynamic)
                {
                    var length = c.Length > 0 ? c.Length : c.Total - c.Start;
                    piece = Math.Min(seconds, length - k * seconds);
                }

                result.Add(Make(c, firstIndex + result.Count, c.Dynamic ? number : firstIndex + result.Count, media, c.Id, bandwidth, number, time, piece, map));
            }

            return result;
        }

        if (lists.Count > 0)
        {
            string? L(string name) => lists.Select(t => (string?)t.Attribute(name)).FirstOrDefault(v => v != null);
            var timescale = Math.Max(1, Long(L("timescale"), 1));
            var duration = Long(L("duration"), 0);
            var startNumber = Long(L("startNumber"), 1);
            var urls = lists[0].Elements().Where(x => x.Name.LocalName == "SegmentURL").ToList();
            var init = lists.Select(l => l.Elements().FirstOrDefault(x => x.Name.LocalName == "Initialization")).FirstOrDefault(x => x != null);
            var map = init != null && (string?)init.Attribute("sourceURL") is { } src ? new HlsMap(Absolute(c.Base, src), null, null) : null;
            var each = duration > 0 ? duration / (double)timescale : (c.Length > 0 && urls.Count > 0 ? c.Length / urls.Count : 0);

            for (var i = 0; i < urls.Count; i++)
            {
                var url = (string?)urls[i].Attribute("media") ?? c.Base.AbsoluteUri;
                long? offset = null, length = null;
                if ((string?)urls[i].Attribute("mediaRange") is { } range && Regex.Match(range, @"^(\d+)-(\d+)$") is { Success: true } m)
                {
                    offset = long.Parse(m.Groups[1].Value);
                    length = long.Parse(m.Groups[2].Value) - offset + 1;
                }

                result.Add(new HlsSegment
                {
                    Index = firstIndex + i,
                    Sequence = firstIndex + i,
                    Url = Absolute(c.Base, url),
                    Duration = each,
                    ByteOffset = offset,
                    ByteLength = length,
                    Map = map,
                });
            }

            return result;
        }

        // a single file: the representation is its BaseURL (on-demand profile)
        if (c.Rep.Elements().Any(e => e.Name.LocalName == "BaseURL") || c.Set.Elements().Any(e => e.Name.LocalName == "BaseURL"))
        {
            result.Add(new HlsSegment { Index = firstIndex, Sequence = firstIndex, Url = c.Base, Duration = Math.Max(c.Length, 0), IsWholeFile = true });
        }

        return result;
    }

    static HlsSegment Make(PeriodContext c, int index, long sequence, string template, string id, long bandwidth, long number, long time,
        double duration, HlsMap? map) =>
        new()
        {
            Index = index,
            Sequence = sequence,
            Url = Absolute(c.Base, Expand(template, id, bandwidth, number, time)),
            Duration = duration,
            Map = map,
        };

    static readonly Regex Variable = new(@"\$(RepresentationID|Number|Bandwidth|Time)(%0(\d+)d)?\$|\$\$", RegexOptions.Compiled);

    /// <summary>Fills in <c>$Number$</c>, <c>$Time$</c>, <c>$RepresentationID$</c>, <c>$Bandwidth$</c> (with <c>%05d</c> widths) and <c>$$</c>.</summary>
    public static string Expand(string template, string id, long bandwidth, long number, long time) =>
        Variable.Replace(template, m =>
        {
            if (m.Value == "$$") return "$";
            var width = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            var value = m.Groups[1].Value switch
            {
                "RepresentationID" => id,
                "Number" => number.ToString(CultureInfo.InvariantCulture),
                "Bandwidth" => bandwidth.ToString(CultureInfo.InvariantCulture),
                _ => time.ToString(CultureInfo.InvariantCulture),
            };
            return width > 0 ? value.PadLeft(width, '0') : value;
        });

    // ---- helpers ----

    static string? KindOf(XElement set, XElement rep)
    {
        var type = ((string?)set.Attribute("contentType") ?? (string?)rep.Attribute("contentType"))?.ToLowerInvariant();
        var mime = (Attr("mimeType", rep, set) ?? "").ToLowerInvariant();
        var codecs = (Attr("codecs", rep, set) ?? "").ToLowerInvariant();
        if (type == "video" || mime.StartsWith("video/")) return "video";
        if (type == "audio" || mime.StartsWith("audio/")) return "audio";
        if (type == "text" || mime.StartsWith("text/") || mime.Contains("ttml") || codecs is "stpp" or "wvtt" || codecs.StartsWith("stpp.")) return "text";
        return null;
    }

    static string? Attr(string name, XElement rep, XElement set) => (string?)rep.Attribute(name) ?? (string?)set.Attribute(name);

    static Uri ResolveBase(Uri parent, XElement element)
    {
        var baseUrl = element.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseURL")?.Value.Trim();
        return !string.IsNullOrEmpty(baseUrl) && Uri.TryCreate(parent, baseUrl, out var resolved) ? resolved : parent;
    }

    static Uri Absolute(Uri baseUri, string reference) =>
        Uri.TryCreate(baseUri, reference, out var uri) ? uri : throw new HlsFormatException("Bad segment address: " + reference);

    static double Duration(string? text, double fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try
        {
            return XmlConvert.ToTimeSpan(text).TotalSeconds;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    static long Long(string? text, long fallback) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    static int Int(string? text) => (int)Math.Min(int.MaxValue, Long(text, 0));
}
