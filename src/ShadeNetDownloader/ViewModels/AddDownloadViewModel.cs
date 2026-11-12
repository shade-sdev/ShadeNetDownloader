using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Hls;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Sites;
using ShadeNet.Core.Torrents;
using ShadeNet.Core.Util;
using ShadeNetDownloader.Services.Browser;
using ShadeNetDownloader.Util;

namespace ShadeNetDownloader.ViewModels;

/// <summary>One quality of a video stream, as the dialog lists it.</summary>
public sealed record QualityOption(string Label, StreamChoice Choice, long Bandwidth, YtDlpOption? Source = null);

/// <summary>One file of a torrent, as the dialog lists it: ticked ones are downloaded.</summary>
public sealed partial class TorrentFileOption : ObservableObject
{
    readonly Action _changed;

    public TorrentFileOption(int index, string path, long length, Action changed)
    {
        Index = index;
        Path = path;
        Length = length;
        _changed = changed;
    }

    /// <summary>Its place in the torrent (what the engine is told).</summary>
    public int Index { get; }
    public string Path { get; }
    public long Length { get; }
    public string SizeText => ByteFormat.Size(Length);

    [ObservableProperty] bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

public enum ProbeState
{
    Idle,
    Probing,
    Ready,
    Failed,
}

/// <summary>
/// State behind the Add dialog: paste links, see what the server says about the file (name, size, resumable), pick
/// where it goes and how aggressively to download it. One link is probed live; several links are added as a batch.
/// </summary>
public sealed partial class AddDownloadViewModel : ObservableObject, IDisposable
{
    readonly DownloadManager _manager;
    readonly CaptureInfo? _capture;
    readonly Func<string, CancellationToken, Task<YtDlpInfo>> _resolver;
    YtDlpInfo? _yt;
    CancellationTokenSource? _probeCts;
    ProbeResult? _probe;
    bool _nameEdited;
    bool _folderEdited;
    bool _settingFromCode;

    /// <param name="resolver">Asks yt-dlp what is behind a video page; tests pass a fake.</param>
    public AddDownloadViewModel(DownloadManager manager, IReadOnlyList<string>? prefill, CaptureInfo? capture = null,
        Func<string, CancellationToken, Task<YtDlpInfo>>? resolver = null)
    {
        _manager = manager;
        _resolver = resolver ?? ((url, ct) => DefaultResolveAsync(manager.Settings, url, PageSignIn(url), ct));
        _connections = Math.Clamp(manager.Settings.DefaultConnections, 1, 32);
        _saveFolder = manager.Settings.ResolveCategoryFolder(CategoryResolver.General);
        _category = CategoryResolver.General;

        _capture = capture;
        if (capture != null)
        {
            // The browser's session first, then the link: the probe fires on the link and must already see the cookies.
            _referer = capture.Referrer ?? "";
            _cookies = capture.Cookies ?? "";
            prefill = new[] { capture.Url };
        }

        if (prefill is { Count: > 0 }) UrlText = string.Join(Environment.NewLine, prefill);
    }

    // ---- input ----

    [ObservableProperty] string _urlText = "";
    [ObservableProperty] string _referer = "";
    [ObservableProperty] string _cookies = "";
    [ObservableProperty] int _connections;
    [ObservableProperty] bool _showAdvanced;

    // ---- derived from the links ----

    public IReadOnlyList<string> Links { get; private set; } = Array.Empty<string>();
    [ObservableProperty] int _linkCount;
    [ObservableProperty] string? _duplicateWarning;

    public bool IsSingle => LinkCount == 1;
    public bool IsBatch => LinkCount > 1;

    // ---- probe result (single link) ----

    [ObservableProperty] ProbeState _probeState;
    [ObservableProperty] string _probeError = "";
    [ObservableProperty] string _fileName = "";
    [ObservableProperty] string _sizeText = "";
    [ObservableProperty] string _typeText = "";
    [ObservableProperty] bool _resumable;
    [ObservableProperty] ImageSource? _icon;

    // ---- a video stream (HLS) ----

    HlsStreamInfo? _stream;
    StreamChoice? _direct;

    /// <summary>The sound's own playlist when the page's player asked for picture and sound separately (sent by the extension).</summary>
    string? SoundPlaylist => _capture is { Source: "media", AudioUrl: { Length: > 0 } sound } && Links.Count > 0 && IsCapturedLink(Links[0]) ? sound : null;

    /// <summary>The addresses the browser's player loaded the captured list of qualities' playlists from (it may add a key of its own).</summary>
    IReadOnlyList<string>? PlayedPlaylists => _capture is { Source: "media", Played: { Count: > 0 } played } && Links.Count > 0 && IsCapturedLink(Links[0]) ? played : null;

    public ObservableCollection<QualityOption> Qualities { get; } = new();

    [ObservableProperty] QualityOption? _selectedQuality;
    [ObservableProperty] bool _isStream;

    /// <summary>Why this stream cannot be added (live, protected); the Add buttons stay off.</summary>
    [ObservableProperty] string? _streamBlock;

    /// <summary>The stream is live: it is recorded until it ends or the user stops it.</summary>
    [ObservableProperty] bool _isLiveStream;

    /// <summary>The stream has subtitle tracks; they can be saved next to the video.</summary>
    [ObservableProperty] string? _subtitleText;

    [ObservableProperty] bool _includeSubtitles = true;

    /// <summary>Put the subtitles inside the video file (MP4/MKV) instead of beside it.</summary>
    [ObservableProperty] bool _embedSubtitles;

    public bool HasSubtitles => SubtitleText != null;

    partial void OnSubtitleTextChanged(string? value) => OnPropertyChanged(nameof(HasSubtitles));

    /// <summary>More than one quality to choose from.</summary>
    public bool HasQualities => IsStream && Qualities.Count > 1;

    partial void OnIsStreamChanged(bool value) => OnPropertyChanged(nameof(HasQualities));
    partial void OnStreamBlockChanged(string? value) => OnPropertyChanged(nameof(CanAdd));
    partial void OnSelectedQualityChanged(QualityOption? value)
    {
        UpdateStreamSize();
        if (_yt != null && value?.Source is { } src && !_nameEdited)
            Run(() => FileName = FileNameHelper.Sanitize(_yt.Title) + src.Extension);   // the container follows the quality (mp4 or mkv)
    }

    // ---- a torrent ----

    TorrentInfo? _torrent;

    /// <summary>The link is a torrent (a magnet link, or a .torrent file on this computer or the web).</summary>
    [ObservableProperty] bool _isTorrent;

    /// <summary>The torrent's files, to pick from (shown when there are several).</summary>
    public ObservableCollection<TorrentFileOption> TorrentFiles { get; } = new();

    public bool HasTorrentFiles => IsTorrent && TorrentFiles.Count > 1;

    /// <summary>What ShadeNet does with a torrent once it is finished (the sharing limits from Settings).</summary>
    [ObservableProperty] string? _torrentNote;

    /// <summary>A .torrent file on the web: download what it holds (on), or save the .torrent file itself (off).</summary>
    [ObservableProperty] bool _downloadTorrentContents = true;

    /// <summary>The torrent came from a web address, so it could be saved as a file instead.</summary>
    [ObservableProperty] bool _isWebTorrent;

    /// <summary>Fetch the pieces in order, so a film can be watched while the rest arrives.</summary>
    [ObservableProperty] bool _torrentSequential;

    /// <summary>
    /// Who asks for a .torrent file on the web: the browser's sign-in, page and identity when the browser handed the link over, else what
    /// was typed under Advanced. Nothing for a magnet link or a file on this computer.
    /// </summary>
    RequestSpec? TorrentWeb(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        return IsCapturedLink(url)
            ? new RequestSpec(url, RefererValue, CookieValue, _capture!.UserAgent, _capture.Headers)
            : new RequestSpec(url, RefererValue, CookieValue);
    }

    /// <summary>"12 of 14 files · 3.1 GB" for the files ticked.</summary>
    [ObservableProperty] string _torrentSelectionText = "";

    /// <summary>What the progress ring says while the link is checked.</summary>
    [ObservableProperty] string _probingText = "Checking the link…";

    /// <summary>A magnet link still waiting for its file list: it can be added anyway (it downloads every file).</summary>
    bool WaitingForPeers => IsTorrent && _torrent == null && ProbeState == ProbeState.Probing;

    /// <summary>Connections per download: not a torrent's business (it talks to however many peers there are).</summary>
    public bool ShowConnections => !IsTorrent;

    partial void OnIsTorrentChanged(bool value)
    {
        OnPropertyChanged(nameof(HasTorrentFiles));
        OnPropertyChanged(nameof(ShowConnections));
        OnPropertyChanged(nameof(CanAdd));
    }

    partial void OnDownloadTorrentContentsChanged(bool value) => OnPropertyChanged(nameof(CanAdd));

    void TorrentSelectionChanged()
    {
        var picked = TorrentFiles.Where(f => f.IsSelected).ToList();
        TorrentSelectionText = $"{picked.Count} of {TorrentFiles.Count} files  ·  {ByteFormat.Size(picked.Sum(f => f.Length))}";
        if (_torrent != null) Run(() => SizeText = ByteFormat.Size(picked.Sum(f => f.Length)) + (picked.Count < TorrentFiles.Count ? $" of {ByteFormat.Size(_torrent.Size)}" : ""));
        if (!_folderEdited && picked.Count > 0)
        {
            Category = _manager.Settings.CategoryOf(picked.OrderByDescending(f => f.Length).First().Path);
            SetFolderForCategory(Category);
        }

        OnPropertyChanged(nameof(CanAdd));
    }

    [RelayCommand]
    void SelectAllTorrentFiles()
    {
        foreach (var f in TorrentFiles) f.IsSelected = true;
    }

    [RelayCommand]
    void SelectNoTorrentFiles()
    {
        foreach (var f in TorrentFiles) f.IsSelected = false;
    }

    /// <summary>"Open .torrent file…": picks a .torrent file on this computer as the link.</summary>
    [RelayCommand]
    void OpenTorrentFile()
    {
        var dialog = new OpenFileDialog { Title = "Open a .torrent file", Filter = "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*", Multiselect = true };
        if (dialog.ShowDialog() == true) UrlText = string.Join(Environment.NewLine, dialog.FileNames);
    }

    string SharingNote()
    {
        var s = _manager.Settings;
        if (s.TorrentSeedRatio <= 0) return "When it finishes, ShadeNet stops: sharing with others is off (Settings > Torrents).";
        var limit = s.TorrentSeedMinutes > 0
            ? $" or for {(s.TorrentSeedMinutes % 60 == 0 ? $"{s.TorrentSeedMinutes / 60} hour{(s.TorrentSeedMinutes == 60 ? "" : "s")}" : $"{s.TorrentSeedMinutes} minutes")}, whichever comes first"
            : "";
        return $"Downloaded from the people sharing it. When it finishes, ShadeNet shares it back until it has sent {s.TorrentSeedRatio:0.##}× its size{limit} (Settings > Torrents).";
    }

    /// <summary>A torrent: its file list (from the peers, for a magnet link), so the user can pick the files.</summary>
    async Task ShowTorrentAsync(string url, CancellationTokenSource cts)
    {
        var magnet = url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
        IsTorrent = true;
        IsWebTorrent = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
        TorrentNote = SharingNote();
        ProbingText = magnet ? "Getting the file list from the people sharing it… (you can add it now: it then downloads every file)" : "Reading the torrent…";
        OnPropertyChanged(nameof(CanAdd));

        TorrentInfo info;
        try
        {
            info = await _manager.DescribeTorrentAsync(url, TorrentWeb(url), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is DownloadException or HttpRequestException or System.IO.IOException or UnauthorizedAccessException)
        {
            if (cts.IsCancellationRequested) return;
            ProbeError = ex is HttpRequestException ? "Can't reach that address. Check the link and your connection." : ex.Message;
            ProbeHint = "";
            ProbeState = ProbeState.Failed;
            return;
        }

        if (cts.IsCancellationRequested) return;
        _torrent = info;
        TorrentFiles.Clear();
        for (var i = 0; i < info.Files.Count; i++) TorrentFiles.Add(new TorrentFileOption(i, info.Files[i].Path, info.Files[i].Length, TorrentSelectionChanged));
        OnPropertyChanged(nameof(HasTorrentFiles));

        var name = FileNameHelper.Sanitize(info.Files.Count == 1 ? System.IO.Path.GetFileName(info.Files[0].Path) : info.Name, "torrent");
        Run(() =>
        {
            if (!_nameEdited) FileName = name;
            TypeText = "Torrent" + (info.Files.Count > 1 ? $"  ·  {info.Files.Count} files, saved in a folder" : "") + (info.IsPrivate ? "  ·  private" : "");
            Resumable = true;
        });
        Icon = FileIconCache.Get(info.Files.Count == 1 ? name : info.Files.OrderByDescending(f => f.Length).First().Path);
        TorrentSelectionChanged();
        ProbeState = ProbeState.Ready;
        _ = CountSwarmAsync(info, cts);
    }

    /// <summary>How many share the torrent, as its trackers say (asked while the dialog is open; null when there is nothing to say).</summary>
    [ObservableProperty] string? _torrentSwarmText;

    async Task CountSwarmAsync(TorrentInfo info, CancellationTokenSource cts)
    {
        TorrentSwarmText = "Asking its trackers how many share it…";
        try
        {
            var c = await _manager.CountTorrentSwarmAsync(info, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;
            TorrentSwarmText = c.Asked == 0
                ? "It names no tracker: its peers come from the DHT and from other peers."
                : c.Answered == 0
                    ? c.Asked == 1 ? "Its tracker did not say how many share it." : $"None of its {c.Asked} trackers said how many share it."
                    : $"{c.Seeds:N0} share all of it and {c.Leechers:N0} are downloading it, "
                      + (c.Answered == 1 && c.Asked == 1 ? "as its tracker says." : $"as {c.Answered} of its {c.Asked} trackers say.");
        }
        catch (OperationCanceledException)
        {
            // the dialog moved on
        }
        catch (Exception ex) when (ex is DownloadException or InvalidOperationException or System.IO.IOException)
        {
            TorrentSwarmText = null;
        }
    }

    // ---- destination ----

    [ObservableProperty] string _category;
    [ObservableProperty] string _saveFolder;

    /// <summary>Shown under a failed probe: what to try next.</summary>
    [ObservableProperty] string _probeHint = DefaultHint;

    const string DefaultHint = "You can still add it - for example if the site needs the cookies below.";

    const string SessionHint =
        "In your browser: open the page, press F12, switch to Network, start the download, click that request and copy "
        + "the Cookie value from Request Headers. Paste it under Advanced below. (The browser extension will do this for you later.)";

    /// <summary>Shown when the link came from the browser extension: the session travels with it.</summary>
    public string? SessionNote => _capture == null
        ? null
        : _capture.Cookies != null
            ? "Taken over from your browser, with its sign-in session and referring page."
            : "Taken over from your browser.";

    /// <summary>The captured request identity applies to the captured link only, not to links the user types in later.</summary>
    bool IsCapturedLink(string url) => _capture != null && string.Equals(_capture.Url, url, StringComparison.Ordinal);

    string? RefererValue => string.IsNullOrWhiteSpace(Referer) ? null : Referer.Trim();
    string? CookieValue => CookieHeader.Normalize(Cookies);

    public bool CanAdd => LinkCount > 0 && (ProbeState != ProbeState.Probing || WaitingForPeers) && !string.IsNullOrWhiteSpace(SaveFolder) && StreamBlock == null
                          && !(IsTorrent && DownloadTorrentContents && _torrent != null && TorrentFiles.All(f => !f.IsSelected));

    public bool ConnectionsMatter => !(ProbeState == ProbeState.Ready && !Resumable);

    partial void OnUrlTextChanged(string value)
    {
        Links = UrlParser.Extract(value);
        LinkCount = Links.Count;
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(IsBatch));
        UpdateDuplicates();

        _nameEdited = false;
        ResetProbe();
        if (Links.Count == 1) _ = ProbeAsync();
        if (Links.Count != 1) SetFolderForCategory(CategoryResolver.General);
        OnPropertyChanged(nameof(CanAdd));
    }

    partial void OnRefererChanged(string value) => Reprobe();
    partial void OnCookiesChanged(string value) => Reprobe();

    partial void OnProbeStateChanged(ProbeState value)
    {
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(ConnectionsMatter));
    }

    partial void OnSaveFolderChanged(string value)
    {
        if (!_settingFromCode) _folderEdited = true;
        OnPropertyChanged(nameof(CanAdd));
    }

    partial void OnFileNameChanged(string value)
    {
        if (_settingFromCode) return;
        _nameEdited = true;
        Refine(value);
    }

    partial void OnLinkCountChanged(int value) => OnPropertyChanged(nameof(CanAdd));

    void Reprobe()
    {
        if (Links.Count == 1) _ = ProbeAsync();
    }

    void ResetProbe()
    {
        _probeCts?.Cancel();
        _probe?.Discard();
        _probe = null;
        ProbeState = ProbeState.Idle;
        ProbeError = "";
        _stream = null;
        _direct = null;
        _yt = null;
        Qualities.Clear();
        SelectedQuality = null;
        IsStream = false;
        IsLiveStream = false;
        SubtitleText = null;
        StreamBlock = null;
        _torrent = null;
        TorrentFiles.Clear();
        IsTorrent = false;
        IsWebTorrent = false;
        TorrentNote = null;
        TorrentSwarmText = null;
        TorrentSelectionText = "";
        DownloadTorrentContents = true;
        ProbingText = "Checking the link…";
        OnPropertyChanged(nameof(HasTorrentFiles));
        Run(() =>
        {
            FileName = "";
            SizeText = "";
            TypeText = "";
            Resumable = false;
            Icon = null;
        });
    }

    async Task ProbeAsync()
    {
        _probeCts?.Cancel();
        var cts = _probeCts = new CancellationTokenSource();
        var url = Links[0];

        ProbeState = ProbeState.Probing;
        ProbeError = "";

        try
        {
            await Task.Delay(500, cts.Token); // let typing settle before touching the network

            // a torrent: a magnet link, a .torrent file here, or one on the web (by its name, or by the name the browser gave it; it is
            // read with the browser's sign-in when the browser handed it over)
            if (DownloadManager.IsTorrentAddress(url)
                || (IsCapturedLink(url) && _capture!.FileName?.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) == true))
            {
                await ShowTorrentAsync(url, cts).ConfigureAwait(true);
                return;
            }

            if (IsCapturedLink(url) && _capture!.Source == "tracks")
            {
                ShowDirect(_capture);
                return;
            }

            // pieces a player asked for with no playlist: the extension already knows what they are
            if (IsCapturedLink(url) && _capture!.Source == "pieces")
            {
                ShowPieces(_capture);
                return;
            }

            // an endless live response: nothing to ask the server (a request would sit on the stream for ever)
            if (IsCapturedLink(url) && _capture!.Source == "record")
            {
                ShowRecording(_capture);
                return;
            }

            if (YtDlp.IsSupportedPage(url) || (IsCapturedLink(url) && _capture!.Source == "page"))
            {
                await ShowYtDlpAsync(url, cts).ConfigureAwait(true);
                return;
            }

            var captured = IsCapturedLink(url);
            var spec = new RequestSpec(url, RefererValue, CookieValue, captured ? _capture!.UserAgent : null,
                captured ? _capture!.Headers : null);
            // The response stays open: if the user presses "Download now" it becomes the first connection, so sites that
            // allow a link to be requested only once still work (IDM: "start downloading immediately").
            ProbeResult result;
            try
            {
                result = await _manager.ProbeAndHoldAsync(spec, cts.Token);
            }
            catch (HttpStatusException refused) when (refused.StatusCode is 401 or 403
                                                      && (DownloadManager.IsStreamAddress(url) || (captured && _capture!.Source == "media")))
            {
                // a playlist refused when asked for as a file: read it as a playlist, which also tries it without its (expired) token
                await ShowStreamAsync(spec, url, cts).ConfigureAwait(true);
                return;
            }

            if (cts.IsCancellationRequested)
            {
                result.Discard();
                return;
            }

            // a .torrent file the server names only by its type
            if (DownloadManager.IsTorrentAddress(url, result.ContentType))
            {
                result.Discard();
                await ShowTorrentAsync(url, cts).ConfigureAwait(true);
                return;
            }

            // A playlist is not a file to save: it is a video stream, which is downloaded piece by piece.
            if (DownloadManager.IsStreamAddress(url, result.ContentType))
            {
                result.Discard();
                await ShowStreamAsync(spec, url, cts).ConfigureAwait(true);
                return;
            }

            _probe?.Discard();
            _probe = result;
            Run(() =>
            {
                // a video the page's own button sent is named after the video; the address only has the CDN's hash
                var named = captured && _capture!.Source == "video" && !string.IsNullOrWhiteSpace(_capture.FileName);
                if (!_nameEdited) FileName = named ? FileNameHelper.Sanitize(_capture!.FileName!) : result.FileName;
                SizeText = result.TotalSize is { } t ? ByteFormat.Size(t) : "Unknown size";
                TypeText = result.ContentType ?? "";
                Resumable = result.AcceptsRanges;
            });
            Refine(FileName);
            ProbeState = ProbeState.Ready;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // superseded by a newer link
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _probe = null;
            ProbeError = Friendly(ex);
            var needsSession = ex is WebPageException;
            ProbeHint = needsSession ? SessionHint : DefaultHint;
            if (needsSession) ShowAdvanced = true;
            ProbeState = ProbeState.Failed;
        }
    }

    /// <summary>The browser's sign-in for the page the browser handed over (and for no other address), when the app was allowed to receive it.</summary>
    string? PageSignIn(string url) =>
        _capture is { Source: "page", Cookies: { Length: > 0 } cookies } && string.Equals(_capture.Url, url, StringComparison.OrdinalIgnoreCase) ? cookies : null;

    /// <summary>
    /// yt-dlp is asked without the sign-in first: most pages need none, and an account's cookies are not something to hand out without a
    /// reason. Only when it cannot read the page is it asked again with them (a live stream, a private video).
    /// </summary>
    static async Task<YtDlpInfo> DefaultResolveAsync(ShadeNet.Core.Engine.EngineSettings settings, string url, string? signIn, CancellationToken ct)
    {
        var exe = settings.FindYtDlp()
                  ?? throw new DownloadException(JobErrorKind.Tool,
                      "yt-dlp was not found. Run tools\\get-tools.ps1, or choose its location in Settings > Downloads.");
        try
        {
            return await YtDlp.ResolveAsync(exe, url, ct).ConfigureAwait(false);
        }
        catch (DownloadException ex) when (signIn != null && ex.Kind == JobErrorKind.Tool && !ex.Message.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase))
        {
            return await YtDlp.ResolveAsync(exe, url, ct, cookies: signIn).ConfigureAwait(false);
        }
    }

    /// <summary>A YouTube page: yt-dlp says which files are behind it; the qualities become the quality list.</summary>
    async Task ShowYtDlpAsync(string url, CancellationTokenSource cts)
    {
        YtDlpInfo info;
        try
        {
            info = await _resolver(url, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (DownloadException ex)
        {
            ProbeError = ex.Message;
            ProbeHint = "ShadeNet asks yt-dlp what is behind a video page. Keep yt-dlp up to date (Settings > Downloads > Update). Videos that need a sign-in, or that the site protects, cannot be read.";
            ProbeState = ProbeState.Failed;
            return;
        }

        if (cts.IsCancellationRequested) return;

        _probe?.Discard();
        _probe = null;
        _yt = info;

        Qualities.Clear();
        foreach (var o in info.Options)
        {
            var choice = o.IsHls
                ? new StreamChoice()
                : new StreamChoice(Direct: true, AudioUrl: o.AudioUrl, Seconds: info.Seconds, Subtitles: false);
            Qualities.Add(new QualityOption(o.Label, choice, 0, o));
        }

        IsStream = true;
        IsLiveStream = info.IsLive;
        OnPropertyChanged(nameof(HasQualities));
        SelectedQuality = Qualities[0];

        Run(() =>
        {
            if (!_nameEdited) FileName = FileNameHelper.Sanitize(info.Title) + SelectedQuality!.Source!.Extension;
            var site = Uri.TryCreate(url, UriKind.Absolute, out var page) ? page.Host.ToLowerInvariant() : "";
            var youtube = site.Contains("youtube", StringComparison.Ordinal) || site == "youtu.be";
            if (site.StartsWith("www.", StringComparison.Ordinal)) site = site[4..];
            TypeText = youtube ? (info.IsLive ? "Live stream (YouTube)" : "YouTube video")
                : info.IsLive ? $"Live stream ({site})" : $"Video from {site}";
            Resumable = true;
        });
        Refine(FileName);
        UpdateStreamSize();
        ProbeState = ProbeState.Ready;
    }

    /// <summary>A player that keeps its list of pieces itself: the pieces it asked for are downloaded in that order and joined.</summary>
    void ShowPieces(CaptureInfo capture)
    {
        _direct = CaptureProcessor.PiecesChoice(capture);
        IsStream = true;
        var count = (capture.Pieces?.Count ?? 0) + (capture.AudioPieces?.Count ?? 0);
        var name = FileNameHelper.Sanitize(string.IsNullOrWhiteSpace(capture.FileName) ? "video" : capture.FileName!);
        if (!new[] { ".mp4", ".mkv", ".ts" }.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant())) name += ".mp4";
        var length = capture.Duration is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        Run(() =>
        {
            if (!_nameEdited) FileName = name;
            SizeText = (length is { } l ? (l.TotalHours >= 1 ? l.ToString(@"h\:mm\:ss") : l.ToString(@"m\:ss")) + "  ·  " : "") + $"{count} pieces";
            TypeText = "Video from the pieces your browser's player loaded";
            Resumable = true;
        });
        Refine(FileName);
        ProbeState = ProbeState.Ready;
    }

    /// <summary>A live stream the browser's player pulls as one endless response: ShadeNet records it with ffmpeg until it ends or is stopped.</summary>
    void ShowRecording(CaptureInfo capture)
    {
        _direct = new StreamChoice(Subtitles: false, Record: true);
        IsStream = true;
        IsLiveStream = true;
        var name = FileNameHelper.Sanitize(string.IsNullOrWhiteSpace(capture.FileName) ? "live" : capture.FileName!);
        if (!name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)) name += ".mkv";
        Run(() =>
        {
            if (!_nameEdited) FileName = name;
            SizeText = "Live: recorded until you stop it";
            TypeText = "Live stream (recorded as it plays)";
            Resumable = false;
        });
        Refine(FileName);
        ProbeState = ProbeState.Ready;
    }

    /// <summary>Picture and sound the browser's player fetched: there is nothing to probe, the extension already knows what they are.</summary>
    void ShowDirect(CaptureInfo capture)
    {
        _direct = new StreamChoice(Direct: true, AudioUrl: capture.AudioUrl, Seconds: capture.Duration ?? 0, Subtitles: false);
        var total = (capture.Size ?? 0) + (capture.AudioSize ?? 0);
        var length = capture.Duration is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        var lengthText = length is { } l ? (l.TotalHours >= 1 ? l.ToString(@"h\:mm\:ss") : l.ToString(@"m\:ss")) + "  ·  " : "";

        IsStream = true;
        var name = string.IsNullOrWhiteSpace(capture.FileName) ? "video.mp4" : capture.FileName!;
        Run(() =>
        {
            if (!_nameEdited) FileName = name;
            SizeText = total > 0 ? lengthText + ByteFormat.Size(total) : lengthText.TrimEnd(' ', '·');
            TypeText = capture.AudioUrl != null ? "Video and sound from your browser" : "Video from your browser";
            Resumable = true;
        });
        Refine(FileName);
        ProbeState = ProbeState.Ready;
    }

    async Task ShowStreamAsync(RequestSpec spec, string url, CancellationTokenSource cts)
    {
        var info = await _manager.DescribeStreamAsync(spec, cts.Token);
        if (cts.IsCancellationRequested) return;

        _probe?.Discard();
        _probe = null;
        _stream = info;

        Qualities.Clear();
        if (info.Variants.Count == 0)
            Qualities.Add(new QualityOption("Original quality", new StreamChoice(), 0));
        else
            foreach (var v in info.Variants)
                Qualities.Add(new QualityOption(v.Label, new StreamChoice(v.Height, v.Bandwidth), v.Bandwidth));

        IsStream = true;
        IsLiveStream = info.IsLive;
        SubtitleText = info.Subtitles is { Count: > 0 } subs ? "Also save subtitles (" + string.Join(", ", subs.Take(4)) + (subs.Count > 4 ? $" and {subs.Count - 4} more" : "") + ")" : null;
        OnPropertyChanged(nameof(HasQualities));
        SelectedQuality = Qualities[0];

        var captured = IsCapturedLink(url) && !string.IsNullOrWhiteSpace(_capture!.FileName) ? _capture.FileName! : info.SuggestedName;
        var name = FileNameHelper.Sanitize(captured);
        if (!new[] { ".mp4", ".mkv", ".ts" }.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant())) name += ".mp4";

        Run(() =>
        {
            if (!_nameEdited) FileName = name;
            TypeText = info.IsLive ? (info.IsDash ? "Live stream (DASH)" : "Live stream (HLS)") : info.IsDash ? "Video stream (DASH)" : "Video stream (HLS)";
            Resumable = true;
        });
        Refine(FileName);

        StreamBlock = info.Problem;
        if (info.Problem != null)
        {
            ProbeError = info.Problem;
            ProbeHint = "";
            ProbeState = ProbeState.Failed;
            return;
        }

        UpdateStreamSize();
        ProbeState = ProbeState.Ready;
    }

    /// <summary>"12:34 · about 410 MB" for the chosen quality.</summary>
    void UpdateStreamSize()
    {
        if (_yt != null)
        {
            var span = TimeSpan.FromSeconds(_yt.Seconds);
            var len = _yt.IsLive ? "Live: recorded until you stop it" : _yt.Seconds > 0 ? (span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss")) : "";
            var sizeText = SelectedQuality?.Source is { Size: > 0 } picked ? "  ·  about " + ByteFormat.Size(picked.Size) : "";
            Run(() => SizeText = len + sizeText);
            return;
        }

        if (_stream == null) return;
        if (_stream.IsLive)
        {
            Run(() => SizeText = "Live: recorded until you stop it");
            return;
        }

        var d = TimeSpan.FromSeconds(_stream.DurationSeconds);
        var length = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
        var bandwidth = SelectedQuality?.Bandwidth ?? 0;
        var size = bandwidth > 0 ? $"  ·  about {ByteFormat.Size((long)(bandwidth / 8.0 * _stream.DurationSeconds))}" : "";
        Run(() => SizeText = $"{length}{size}");
    }

    /// <summary>File name changed (probe or user): refresh the icon, category and, unless the user chose one, the folder.</summary>
    void Refine(string name)
    {
        Icon = string.IsNullOrWhiteSpace(name) ? null : FileIconCache.Get(name);
        var category = _manager.Settings.CategoryOf(string.IsNullOrWhiteSpace(name) ? "x" : name);
        Category = category;
        if (!_folderEdited) SetFolderForCategory(category);
    }

    void SetFolderForCategory(string category)
    {
        if (_folderEdited) return;
        Run(() => SaveFolder = _manager.Settings.ResolveCategoryFolder(category));
    }

    void Run(Action set)
    {
        var before = _settingFromCode;
        _settingFromCode = true;
        try
        {
            set();
        }
        finally
        {
            _settingFromCode = before;
        }
    }

    void UpdateDuplicates()
    {
        var dupes = Links.Select(l => _manager.FindDuplicate(l)).Where(j => j != null).ToList();
        DuplicateWarning = dupes.Count switch
        {
            0 => null,
            1 when Links.Count == 1 => $"This link is already in your list as “{dupes[0]!.FileName}”. Adding it again creates a second copy.",
            1 => "1 of these links is already in your list.",
            _ => $"{dupes.Count} of these links are already in your list.",
        };
    }

    static string Friendly(Exception ex) => ex switch
    {
        // ShadeNet's guess only when the server said nothing itself (it knows better: a disabled account is no expired link)
        HttpStatusException h => h.Message + (h.Kind == JobErrorKind.AddressExpired && h.ServerReason == null ? " The link may have expired or need you to be signed in." : ""),
        DownloadException d => d.Message,
        HttpRequestException { InnerException: SocketException } => "Can't reach that address. Check the link and your connection.",
        HttpRequestException h => h.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            ? "That web address does not exist." : "Can't reach that address. Check the link and your connection.",
        TaskCanceledException or TimeoutException => "The server took too long to answer.",
        _ => ex.Message,
    };

    // ---- commands ----

    [RelayCommand]
    void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Save downloads to", InitialDirectory = SaveFolder, Multiselect = false };
        if (dialog.ShowDialog() == true) SaveFolder = dialog.FolderName;
    }

    [RelayCommand]
    void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    // ---- result ----

    /// <summary>The requests to hand to the engine, each with the probe result when one is available.</summary>
    public IReadOnlyList<(AddDownloadRequest Request, ProbeResult? Probe)> Build(AddMode mode)
    {
        var referer = RefererValue;
        var cookies = CookieValue;
        var single = Links.Count == 1;

        // a YouTube page: the files behind it (found by yt-dlp) are what gets downloaded, not the page
        var source = single && _yt != null ? SelectedQuality?.Source : null;

        // a torrent: what it holds, the files ticked (all of them while a magnet link's list is not known yet)
        if (single && IsTorrent && DownloadTorrentContents)
        {
            var picked = _torrent != null && TorrentFiles.Any(f => !f.IsSelected) ? TorrentFiles.Where(f => f.IsSelected).Select(f => f.Index).ToList() : null;
            return new[]
            {
                (new AddDownloadRequest
                {
                    Url = Links[0],
                    // (a magnet link added before its file list came: name and folder follow the torrent, unless the user chose them)
                    FileName = (_torrent != null || _nameEdited) && !string.IsNullOrWhiteSpace(FileName) ? FileName.Trim() : null,
                    SaveDirectory = _torrent != null || _folderEdited ? SaveFolder.Trim() : null,
                    Category = _torrent != null ? Category : null,
                    Torrent = new TorrentChoice(_torrent?.Metadata, picked, TorrentSequential),
                    // (a .torrent file on the web is fetched again by the download when it was not read yet: with the same sign-in)
                    Referer = TorrentWeb(Links[0])?.Referer,
                    Cookies = TorrentWeb(Links[0])?.Cookies,
                    UserAgent = TorrentWeb(Links[0])?.UserAgent,
                    Headers = TorrentWeb(Links[0])?.Headers?.ToDictionary(h => h.Key, h => h.Value),
                    Mode = mode,
                }, (ProbeResult?)null),
            };
        }

        return Links.Select(url => (new AddDownloadRequest
        {
            Url = source?.VideoUrl ?? url,
            // the files behind a page: what yt-dlp itself was told to send for them (some sites answer 403 without it),
            // never the browser's own session
            Referer = source != null ? source.Referer : referer,
            Cookies = source != null ? source.Cookies : cookies,
            UserAgent = source?.UserAgent ?? (IsCapturedLink(url) ? _capture!.UserAgent : null),
            Headers = source != null ? source.Headers?.ToDictionary(h => h.Key, h => h.Value)
                : IsCapturedLink(url) ? _capture!.Headers.ToDictionary(h => h.Key, h => h.Value) : null,
            // (the .torrent file itself, saved as a file, keeps the name the address gives it)
            FileName = single && !IsTorrent && !string.IsNullOrWhiteSpace(FileName) ? FileName.Trim() : null,
            SaveDirectory = single || _folderEdited ? SaveFolder.Trim() : null,
            Category = single ? Category : null,
            MaxConnections = Connections,
            Mode = mode,
            Stream = single && _direct != null ? _direct
                : source != null ? SelectedQuality!.Choice
                : single && IsStream ? (SelectedQuality?.Choice ?? new StreamChoice()) with { Subtitles = IncludeSubtitles, EmbedSubtitles = IncludeSubtitles && EmbedSubtitles, AudioUrl = SoundPlaylist, Played = PlayedPlaylists }
                : !single && DownloadManager.IsStreamAddress(url) ? new StreamChoice() : null,
        }, single && !IsStream ? _probe : null)).ToList();
    }

    public void Dispose()
    {
        _probeCts?.Cancel();
        _probe?.Discard();   // no-op when "Download now" already handed the connection to a download
        _probeCts?.Dispose();
    }
}
