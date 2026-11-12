using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Links;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;

namespace ShadeNetDownloader.ViewModels;

/// <summary>One link of the page, as the link grabber lists it.</summary>
public sealed partial class GrabbedLinkRow : ObservableObject
{
    readonly Action _changed;

    public GrabbedLinkRow(GrabbedLink link, string category, Action changed)
    {
        Url = link.Url;
        Name = LinkGrabber.NameOf(link.Url);
        Text = link.Text != null && !string.Equals(link.Text, Name, StringComparison.OrdinalIgnoreCase) ? link.Text : null;
        Host = Uri.TryCreate(link.Url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? u.Host : "magnet link";
        Category = category;
        IsFile = LinkGrabber.LooksLikeFile(link.Url);
        _changed = changed;
    }

    public string Url { get; }
    public string Name { get; }

    /// <summary>The words the page showed for it, when they say more than its name.</summary>
    public string? Text { get; }

    public string Host { get; }
    public string Category { get; }

    /// <summary>It names a file (rather than another page).</summary>
    public bool IsFile { get; }

    public string Caption => Text != null ? $"{Host}  ·  “{Text}”" : Host;

    [ObservableProperty] bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

/// <summary>A kind of file the list can be narrowed to (Video, Archives …), with how many links are of it.</summary>
public sealed partial class LinkKindFilter : ObservableObject
{
    readonly Action _changed;

    public LinkKindFilter(string name, int count, Action changed)
    {
        Name = name;
        Count = count;
        _changed = changed;
    }

    public string Name { get; }
    public int Count { get; }
    public string Label => $"{Name} ({Count})";

    [ObservableProperty] bool _isOn = true;

    partial void OnIsOnChanged(bool value) => _changed();
}

/// <summary>
/// The link grabber: the links of a page (read by ShadeNet, or sent by the browser), narrowed by kind and words, chosen, and added as
/// downloads, with the page as their referrer and the browser's sign-in for each site when the browser sent it. They can go together
/// into a folder named after the page.
/// </summary>
public sealed partial class LinkGrabberViewModel : ObservableObject
{
    readonly DownloadManager _manager;
    string? _page;
    IReadOnlyDictionary<string, string>? _cookies;
    string? _userAgent;
    bool _refreshing;

    public LinkGrabberViewModel(DownloadManager manager) => _manager = manager;

    /// <summary>Every link of the page.</summary>
    public List<GrabbedLinkRow> Rows { get; } = new();

    /// <summary>The links the filters let through, in the page's order.</summary>
    public ObservableCollection<GrabbedLinkRow> Shown { get; } = new();

    public ObservableCollection<LinkKindFilter> Kinds { get; } = new();

    [ObservableProperty] string _pageTitle = "";
    [ObservableProperty] string _pageUrl = "";
    [ObservableProperty] bool _isLoading;
    [ObservableProperty] string? _loadError;
    [ObservableProperty] string _filter = "";

    /// <summary>Only links to files (not to other pages).</summary>
    [ObservableProperty] bool _onlyFiles = true;

    [ObservableProperty] string _summary = "";
    [ObservableProperty] bool _canAdd;

    /// <summary>Put the chosen files in one folder named after the page (under the download folder).</summary>
    [ObservableProperty] bool _intoFolder;

    [ObservableProperty] string _folderName = "";

    public string PrimaryText => SelectedCount == 1 ? "Download 1 file" : $"Download {SelectedCount} files";

    public int SelectedCount => Rows.Count(r => r.IsSelected);

    partial void OnFilterChanged(string value) => Refilter();
    partial void OnOnlyFilesChanged(bool value) => Refilter();

    /// <summary>Reads a page's links (the Add dialog's "Grab the links on this page").</summary>
    public async Task LoadAsync(string url, RequestSpec? web, CancellationToken ct)
    {
        IsLoading = true;
        LoadError = null;
        PageUrl = url;
        PageTitle = "Reading the page…";
        try
        {
            Show(await _manager.GrabPageAsync(url, web, ct).ConfigureAwait(true), null, web?.UserAgent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // closed
        }
        catch (Exception ex) when (ex is DownloadException or HttpRequestException or IOException or OperationCanceledException or UriFormatException)
        {
            PageTitle = "No links";
            LoadError = NetworkAdapterOfflineException.IsIn(ex) ? ex.InnerException?.Message ?? ex.Message
                : ex is HttpRequestException or OperationCanceledException ? "The page could not be read. Check the address and your connection."
                : ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <param name="cookiesByHost">The browser's sign-in for each site of the links (what the extension sends); null when ShadeNet read the page.</param>
    public void Show(GrabbedPage page, IReadOnlyDictionary<string, string>? cookiesByHost, string? userAgent)
    {
        _page = page.Url;
        _cookies = cookiesByHost;
        _userAgent = userAgent;
        PageUrl = page.Url;
        PageTitle = page.Title ?? (Uri.TryCreate(page.Url, UriKind.Absolute, out var u) ? u.Host : page.Url);
        FolderName = LinkGrabber.FolderNameOf(page);

        Rows.Clear();
        foreach (var link in page.Links)
            Rows.Add(new GrabbedLinkRow(link, link.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? "Torrents" : _manager.Settings.CategoryOf(LinkGrabber.NameOf(link.Url)), Changed));

        Kinds.Clear();
        foreach (var group in Rows.Where(r => r.IsFile).GroupBy(r => r.Category).OrderByDescending(g => g.Count()))
            Kinds.Add(new LinkKindFilter(group.Key, group.Count(), Refilter));

        // a page with no file at all: its links are shown (another page may be what is wanted)
        if (Rows.Count > 0 && Rows.All(r => !r.IsFile)) _onlyFiles = false;
        OnPropertyChanged(nameof(OnlyFiles));
        if (Rows.Count == 0) LoadError = "This page has no links to download.";
        Refilter();
    }

    void Refilter()
    {
        if (_refreshing) return;
        var words = Filter.Trim();
        var off = Kinds.Where(k => !k.IsOn).Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Shown.Clear();
        foreach (var row in Rows)
        {
            if (OnlyFiles && !row.IsFile) continue;
            if (row.IsFile && off.Contains(row.Category)) continue;
            if (words.Length > 0 && !(row.Url.Contains(words, StringComparison.OrdinalIgnoreCase) || row.Name.Contains(words, StringComparison.OrdinalIgnoreCase)
                                      || row.Text?.Contains(words, StringComparison.OrdinalIgnoreCase) == true))
                continue;
            Shown.Add(row);
        }

        Changed();
    }

    void Changed()
    {
        var chosen = SelectedCount;
        Summary = $"{Shown.Count} of {Rows.Count} links shown  ·  {chosen} chosen";
        CanAdd = chosen > 0;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(PrimaryText));
    }

    [RelayCommand]
    void SelectShown()
    {
        _refreshing = true;
        foreach (var row in Shown) row.IsSelected = true;
        _refreshing = false;
        Changed();
    }

    [RelayCommand]
    void SelectNone()
    {
        _refreshing = true;
        foreach (var row in Rows) row.IsSelected = false;
        _refreshing = false;
        Changed();
    }

    /// <summary>The chosen links as downloads: the page as their referrer, the browser's sign-in for their site, one folder when asked.</summary>
    public IReadOnlyList<AddDownloadRequest> Build(AddMode mode)
    {
        string? folder = null;
        if (IntoFolder)
        {
            var name = ShadeNet.Core.Util.FileNameHelper.Sanitize(FolderName.Trim(), "Links");
            folder = Path.Combine(_manager.Settings.DownloadRoot, name);
        }

        return Rows.Where(r => r.IsSelected).Select(r =>
        {
            var torrent = DownloadManager.IsTorrentAddress(r.Url);
            string? cookies = null;
            if (_cookies != null && Uri.TryCreate(r.Url, UriKind.Absolute, out var u)) _cookies.TryGetValue(u.Host, out cookies);
            return new AddDownloadRequest
            {
                Url = r.Url,
                Referer = torrent && r.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? null : _page,
                Cookies = cookies,
                UserAgent = _userAgent,
                SaveDirectory = folder,
                Mode = mode,
                Torrent = torrent ? new TorrentChoice() : null,
            };
        }).ToList();
    }
}
