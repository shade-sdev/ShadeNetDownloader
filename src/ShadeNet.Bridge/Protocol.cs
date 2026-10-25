using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShadeNet.Bridge;

/// <summary>Names and limits shared by the app, the native messaging host and (by convention) the extension.</summary>
public static class BridgeProtocol
{
    /// <summary>Native messaging host name; also the registry key / manifest name browsers look up.</summary>
    public const string HostName = "com.shadenet.downloader";

    public const string Version = "1.0";

    /// <summary>Chrome refuses native-to-browser messages above 1 MB; the same cap protects the app from the other direction.</summary>
    public const int MaxFromBrowserBytes = 4 * 1024 * 1024;
    public const int MaxToBrowserBytes = 1024 * 1024;

    /// <summary>
    /// <c>ShadeNetDownloader.Bridge.&lt;SessionId&gt;</c>: one pipe per Windows session, like IDM's
    /// <c>IDMNetworkMonitor.&lt;SessionId&gt;</c> (report §5). <c>SHADENET_PIPE</c> overrides it for tests.
    /// </summary>
    public static string PipeName()
    {
        var overridden = Environment.GetEnvironmentVariable("SHADENET_PIPE");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        var name = "ShadeNetDownloader.Bridge." + Process.GetCurrentProcess().SessionId;

        // An instance running against a throw-away data folder (tests, screenshots) must not be mistaken for the user's
        // own instance, so its pipe is named after that folder.
        var dataDir = Environment.GetEnvironmentVariable("SHADENET_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).ToLowerInvariant()));
            name += "." + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }

        return name;
    }

    /// <summary>The folder the app keeps its data in (settings, database, host log and host config).</summary>
    public static string DataDir()
    {
        var overridden = Environment.GetEnvironmentVariable("SHADENET_DATA_DIR");
        return !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShadeNet");
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Message type names. Every frame is a JSON object with a <c>type</c> member.</summary>
public static class MessageTypes
{
    // extension → app
    public const string Hello = "hello";
    public const string Capture = "capture";
    public const string Ping = "ping";

    // host → app (added by the native host itself)
    public const string HostHello = "host-hello";

    // app → extension
    public const string HelloAck = "hello-ack";
    public const string Settings = "settings";
    public const string CaptureResult = "capture-result";
    public const string Pong = "pong";

    // host → extension
    public const string HostError = "host-error";

    // second app instance → running app
    public const string Activate = "activate";

    /// <summary>"Really quit" (the installer asks a running ShadeNet to end before it replaces or removes files).</summary>
    public const string Quit = "quit";
}

/// <summary>Why a capture was or was not taken over; sent back as <see cref="CaptureResult.Reason"/>.</summary>
public static class CaptureReasons
{
    public const string Added = "added";
    public const string Refreshed = "refreshed";
    public const string Duplicate = "duplicate";
    public const string Disabled = "disabled";
    public const string Invalid = "invalid";
    public const string Excluded = "excluded";
    public const string Error = "error";
}

/// <summary>The extension tells the app about a download the browser started, with the session needed to repeat it.</summary>
public sealed record CaptureRequest
{
    public string Type { get; init; } = MessageTypes.Capture;

    /// <summary>Correlates the reply (<see cref="CaptureResult.Id"/>).</summary>
    public string Id { get; init; } = "";

    /// <summary>The address to download (after the browser's redirects).</summary>
    public string Url { get; init; } = "";

    /// <summary>The address the browser first asked for, when it differs.</summary>
    public string? OriginalUrl { get; init; }

    /// <summary>The page the user clicked on.</summary>
    public string? Referrer { get; init; }

    /// <summary>The <c>Cookie</c> header value the browser sent (or the cookies it holds for the address).</summary>
    public string? Cookies { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>Selected request headers worth replaying (<c>Authorization</c>, <c>Origin</c>, …); filtered by the app.</summary>
    public Dictionary<string, string>? Headers { get; init; }

    public string? FileName { get; init; }
    public string? MimeType { get; init; }
    public long? Size { get; init; }

    /// <summary>
    /// For a video whose picture and sound are separate files (what a video site's player fetches): the sound's address and
    /// size, and the length in seconds. <see cref="Url"/> is then the picture (or a file with both).
    /// </summary>
    public string? AudioUrl { get; init; }
    public long? AudioSize { get; init; }
    public double? Duration { get; init; }

    /// <summary>
    /// With <c>media</c>, a list of qualities: the exact addresses the player loaded the playlists it names from. A player may add
    /// parameters of its own (a key) that the list does not carry, and the server may want them on every request.
    /// </summary>
    public string[]? Played { get; init; }

    /// <summary>
    /// With <c>pieces</c>: what a player that keeps its list of pieces itself (no playlist or manifest) asked for, in that order, and the
    /// initialisation part they follow; <see cref="AudioPieces"/> and <see cref="AudioInit"/> when the sound is a run of its own.
    /// </summary>
    public string[]? Pieces { get; init; }
    public string? Init { get; init; }
    public string[]? AudioPieces { get; init; }
    public string? AudioInit { get; init; }

    /// <summary><c>download</c> (browser started it) or <c>contextmenu</c> (user chose "Download with ShadeNet").</summary>
    public string Source { get; init; } = "download";
}

public sealed record CaptureResult
{
    public string Type { get; init; } = MessageTypes.CaptureResult;
    public string Id { get; init; } = "";

    /// <summary>True when the app took responsibility: the extension may cancel and erase the browser's own download.</summary>
    public bool Accepted { get; init; }

    public string Reason { get; init; } = "";
    public string? JobId { get; init; }
}

/// <summary>What the app tells the extension about itself and about how it wants captures filtered.</summary>
public sealed record BridgeSettings
{
    public string Type { get; init; } = MessageTypes.HelloAck;
    public string AppVersion { get; init; } = "";
    public string ProtocolVersion { get; init; } = BridgeProtocol.Version;

    /// <summary>Master switch of the integration in the app.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Hosts (and their sub-domains) the browser keeps for itself.</summary>
    public string[] ExcludedHosts { get; init; } = Array.Empty<string>();

    /// <summary>Downloads known to be smaller than this stay with the browser (0 = take everything).</summary>
    public long MinSizeBytes { get; init; }

    /// <summary>The extension sends the page site's sign-in along with a video page, so that yt-dlp can read what needs one (most live streams).</summary>
    public bool PageSignIn { get; init; } = true;
}

public sealed record HostHello
{
    public string Type { get; init; } = MessageTypes.HostHello;
    public string? Origin { get; init; }
    public string HostVersion { get; init; } = BridgeProtocol.Version;
    public int ProcessId { get; init; } = Environment.ProcessId;
}

public sealed record HostError
{
    public string Type { get; init; } = MessageTypes.HostError;
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>A received frame: its <c>type</c> and the parsed JSON.</summary>
public sealed class BridgeMessage
{
    public BridgeMessage(string type, JsonElement body)
    {
        Type = type;
        Body = body;
    }

    public string Type { get; }
    public JsonElement Body { get; }

    public T? As<T>() => Body.Deserialize<T>(BridgeProtocol.Json);

    public static BridgeMessage? Parse(ReadOnlyMemory<byte> utf8)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var type = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return string.IsNullOrEmpty(type) ? null : new BridgeMessage(type, doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
