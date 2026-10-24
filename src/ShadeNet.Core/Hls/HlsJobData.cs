using System.Text.Json;

namespace ShadeNet.Core.Hls;

/// <summary>
/// What a stream download remembers between runs (stored as JSON with the job): which quality was chosen, and how far
/// it got, so a paused or restarted download shows its progress before the playlist has been fetched again.
/// The finished segments themselves are files in the job's work folder; their presence is the real progress.
/// </summary>
public sealed class HlsJobData
{
    /// <summary>The chosen quality (0 = the playlist is a single stream, or "the best").</summary>
    public int Height { get; set; }
    public long Bandwidth { get; set; }

    /// <summary>Name of the chosen audio track when the stream keeps audio apart; null = the group's default.</summary>
    public string? AudioName { get; set; }

    /// <summary>Also save subtitle tracks as sidecar files.</summary>
    public bool Subtitles { get; set; } = true;

    /// <summary>Subtitles go inside the video file when that is possible (otherwise they stay beside it).</summary>
    public bool EmbedSubtitles { get; set; }

    /// <summary>The job is the picture and sound a video site's player fetched as separate files, not a playlist.</summary>
    public bool Direct { get; set; }

    public string? AudioUrl { get; set; }

    /// <summary>The length of a direct download in seconds.</summary>
    public double Seconds { get; set; }

    /// <summary>The stream is live: it is recorded until it ends or the user stops it.</summary>
    public bool Live { get; set; }

    /// <summary>
    /// The job's address is one endless response (a live FLV pull) that ffmpeg records as it arrives, not a playlist of pieces:
    /// there are no segments, so nothing to resume; stopping the recording saves it.
    /// </summary>
    public bool Record { get; set; }

    /// <summary>
    /// No playlist: the pieces a player asked for (it keeps their list itself), in that order, after their initialisation part
    /// (none for MPEG-TS pieces). The sound's own run when it is kept apart. They are downloaded and joined like a playlist's pieces.
    /// </summary>
    public List<string>? Pieces { get; set; }

    public string? InitUrl { get; set; }
    public List<string>? AudioPieces { get; set; }
    public string? AudioInitUrl { get; set; }

    // progress
    public long BytesDone { get; set; }
    public int Parts { get; set; }
    public int PartsDone { get; set; }
    public double SecondsDone { get; set; }
    public double SecondsTotal { get; set; }

    /// <summary>The expected size of the finished download, refined as segments arrive; null until a guess is possible.</summary>
    public long? EstimatedTotal { get; set; }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static HlsJobData FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HlsJobData();
        try
        {
            return JsonSerializer.Deserialize<HlsJobData>(json, Options) ?? new HlsJobData();
        }
        catch (JsonException)
        {
            return new HlsJobData();
        }
    }
}
