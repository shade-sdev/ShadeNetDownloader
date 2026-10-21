using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Tools;

namespace ShadeNet.Core.Tests;

public class ToolManagerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "shadenet-tm-" + Guid.NewGuid().ToString("N")[..8]);

    public ToolManagerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // best effort
        }
    }

    string App => Path.Combine(_root, "app");
    string User => Path.Combine(_root, "user-tools");

    /// <summary>A web server in a variable: answers by address; any address it does not know is a test failure.</summary>
    sealed class FakeWeb : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new();
        public List<string> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Asked.Add(url);
            return Task.FromResult(Routes.TryGetValue(url, out var make)
                ? make()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public void Json(string url, string json) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        public void Bytes(string url, byte[] bytes) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        public void Text(string url, string text) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };
    }

    ToolManager Manager(FakeWeb web, Func<ManagedTool, string, CancellationToken, Task<string?>>? versionOf = null, Action<EngineSettings>? tweak = null)
    {
        var settings = new EngineSettings();
        tweak?.Invoke(settings);
        return new ToolManager(settings, User, App, new HttpClient(web), versionOf ?? ((_, _, _) => Task.FromResult<string?>("2026.08.19")), pathVariable: "");
    }

    string Put(string folder, string name, string content = "exe")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    const string YtApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    const string FfApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";

    // ---- versions ----

    [Theory]
    [InlineData("2026.09.01", "2026.08.19", true)]
    [InlineData("2026.08.19.1", "2026.08.19", true)]
    [InlineData("2026.08.19", "2026.08.19", false)]
    [InlineData("2026.08.18", "2026.08.19", false)]
    [InlineData("2027.01.01", "2026.12.31", true)]
    [InlineData("2026.10.01", "2026.9.30", true)]
    public void Versions_compare_number_by_number(string candidate, string current, bool newer) =>
        Assert.Equal(newer, ToolManager.IsNewerVersion(candidate, current));

    // ---- which copy is used ----

    [Fact]
    public void The_users_choice_wins_then_a_downloaded_update_then_the_bundled_copy()
    {
        var bundled = Put(Path.Combine(App, "tools", "yt-dlp"), "yt-dlp.exe");
        var m = Manager(new FakeWeb());
        Assert.Equal((bundled, ToolSource.Bundled), (m.Find(ManagedTool.YtDlp), m.SourceOf(ManagedTool.YtDlp)));

        var updated = Put(Path.Combine(User, "yt-dlp"), "yt-dlp.exe");
        Assert.Equal((updated, ToolSource.User), (m.Find(ManagedTool.YtDlp), m.SourceOf(ManagedTool.YtDlp)));

        var chosen = Put(Path.Combine(_root, "mine"), "yt-dlp.exe");
        var mine = Manager(new FakeWeb(), tweak: s => s.YtDlpPath = chosen);
        Assert.Equal((chosen, ToolSource.Custom), (mine.Find(ManagedTool.YtDlp), mine.SourceOf(ManagedTool.YtDlp)));

        Assert.Equal(ToolSource.None, Manager(new FakeWeb()).SourceOf(ManagedTool.Ffmpeg));
    }

    // ---- looking for updates ----

    [Fact]
    public async Task A_newer_yt_dlp_is_offered_and_the_same_or_an_older_one_is_not()
    {
        Put(Path.Combine(App, "tools", "yt-dlp"), "yt-dlp.exe");
        var web = new FakeWeb();
        web.Json(YtApi, """{"tag_name":"2026.09.01"}""");
        var m = Manager(web);

        var update = await m.CheckAsync(ManagedTool.YtDlp, default);
        Assert.NotNull(update);
        Assert.Equal(("2026.08.19", "2026.09.01"), (update!.Current, update.Latest));
        Assert.Equal("https://github.com/yt-dlp/yt-dlp/releases/download/2026.09.01/yt-dlp.exe", update.AssetUrl);
        Assert.EndsWith("/SHA2-256SUMS", update.ChecksumUrl);

        web.Json(YtApi, """{"tag_name":"2026.08.19"}""");
        Assert.Null(await m.CheckAsync(ManagedTool.YtDlp, default));
        web.Json(YtApi, """{"tag_name":"2026.07.01"}""");
        Assert.Null(await m.CheckAsync(ManagedTool.YtDlp, default));
    }

    [Fact]
    public async Task A_copy_the_user_chose_is_never_checked_or_touched()
    {
        var chosen = Put(Path.Combine(_root, "mine"), "yt-dlp.exe");
        var web = new FakeWeb();
        var m = Manager(web, tweak: s => s.YtDlpPath = chosen);

        Assert.Null(await m.CheckAsync(ManagedTool.YtDlp, default));
        Assert.Empty(web.Asked);                                              // not even a look at the release page
    }

    [Fact]
    public async Task A_tool_that_is_not_installed_can_be_offered_for_download()
    {
        var web = new FakeWeb();
        web.Json(YtApi, """{"tag_name":"2026.09.01"}""");
        var update = await Manager(web).CheckAsync(ManagedTool.YtDlp, default);
        Assert.Null(update!.Current);
    }

    [Fact]
    public async Task ffmpeg_is_only_offered_when_the_build_is_a_month_newer_than_the_installed_one()
    {
        var exe = Put(Path.Combine(App, "tools", "ffmpeg"), "ffmpeg.exe");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "tool.json"), """{"version":"2026-01-01","published":"2026-01-01T00:00:00Z"}""");
        var web = new FakeWeb();
        var m = Manager(web);

        web.Json(FfApi, """{"assets":[{"name":"ffmpeg-master-latest-win64-lgpl.zip","updated_at":"2026-01-20T00:00:00Z"}]}""");
        Assert.Null(await m.CheckAsync(ManagedTool.Ffmpeg, default));         // 19 days: not worth a question

        web.Json(FfApi, """{"assets":[{"name":"ffmpeg-master-latest-win64-lgpl.zip","updated_at":"2026-03-01T00:00:00Z"}]}""");
        var update = await m.CheckAsync(ManagedTool.Ffmpeg, default);
        Assert.Equal(("2026-01-01", "2026-03-01"), (update!.Current, update.Latest));
        Assert.EndsWith("ffmpeg-master-latest-win64-lgpl.zip", update.AssetUrl);
    }

    // ---- installing ----

    [Fact]
    public async Task An_update_is_verified_installed_in_the_users_folder_and_used_from_then_on()
    {
        var bundled = Put(Path.Combine(App, "tools", "yt-dlp"), "yt-dlp.exe", "old program");
        var newProgram = Encoding.UTF8.GetBytes("the new program");
        var web = new FakeWeb();
        web.Json(YtApi, """{"tag_name":"2026.09.01"}""");
        const string baseUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/2026.09.01";
        web.Bytes($"{baseUrl}/yt-dlp.exe", newProgram);
        web.Text($"{baseUrl}/SHA2-256SUMS", $"{new string('0', 64)}  yt-dlp_x86.exe\n{Sha(newProgram)}  yt-dlp.exe\n");
        var m = Manager(web, (_, exe, _) => Task.FromResult<string?>(File.ReadAllText(exe) == "the new program" ? "2026.09.01" : "2026.08.19"));

        var update = (await m.CheckAsync(ManagedTool.YtDlp, default))!;
        var version = await m.UpdateAsync(update, default);

        Assert.Equal("2026.09.01", version);
        var installed = Path.Combine(User, "yt-dlp", "yt-dlp.exe");
        Assert.Equal("the new program", File.ReadAllText(installed));
        Assert.Equal("old program", File.ReadAllText(bundled));                // the installed program is never touched
        Assert.Equal((installed, ToolSource.User), (m.Find(ManagedTool.YtDlp), m.SourceOf(ManagedTool.YtDlp)));
        Assert.Contains("2026.09.01", File.ReadAllText(Path.Combine(User, "yt-dlp", "tool.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(User, "yt-dlp"), "staged-*"));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_installs_nothing()
    {
        Put(Path.Combine(App, "tools", "yt-dlp"), "yt-dlp.exe");
        var web = new FakeWeb();
        web.Json(YtApi, """{"tag_name":"2026.09.01"}""");
        const string baseUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/2026.09.01";
        web.Bytes($"{baseUrl}/yt-dlp.exe", Encoding.UTF8.GetBytes("tampered"));
        web.Text($"{baseUrl}/SHA2-256SUMS", $"{Sha(Encoding.UTF8.GetBytes("the real one"))}  yt-dlp.exe\n");
        var m = Manager(web);

        var ex = await Assert.ThrowsAsync<DownloadException>(async () => await m.UpdateAsync((await m.CheckAsync(ManagedTool.YtDlp, default))!, default));
        Assert.Equal(JobErrorKind.DataCorruption, ex.Kind);
        Assert.Contains("checksum", ex.Message);
        Assert.False(File.Exists(Path.Combine(User, "yt-dlp", "yt-dlp.exe")));
    }

    [Fact]
    public async Task A_new_program_that_does_not_start_never_replaces_one_that_works()
    {
        Put(Path.Combine(User, "yt-dlp"), "yt-dlp.exe", "works");
        var bad = Encoding.UTF8.GetBytes("broken download");
        var web = new FakeWeb();
        web.Json(YtApi, """{"tag_name":"2026.09.01"}""");
        const string baseUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/2026.09.01";
        web.Bytes($"{baseUrl}/yt-dlp.exe", bad);
        web.Text($"{baseUrl}/SHA2-256SUMS", $"{Sha(bad)}  yt-dlp.exe\n");
        var m = Manager(web, (_, exe, _) => Task.FromResult<string?>(File.ReadAllText(exe) == "works" ? "2026.08.19" : null));

        var ex = await Assert.ThrowsAsync<DownloadException>(async () => await m.UpdateAsync((await m.CheckAsync(ManagedTool.YtDlp, default))!, default));
        Assert.Equal(JobErrorKind.Tool, ex.Kind);
        Assert.Equal("works", File.ReadAllText(Path.Combine(User, "yt-dlp", "yt-dlp.exe")));
        Assert.Empty(Directory.GetFiles(Path.Combine(User, "yt-dlp"), "staged-*"));
    }

    [Fact]
    public async Task ffmpeg_is_unpacked_from_the_zip_it_comes_in()
    {
        var web = new FakeWeb();
        web.Json(FfApi, """{"assets":[{"name":"ffmpeg-master-latest-win64-lgpl.zip","updated_at":"2026-03-01T00:00:00Z"}]}""");
        byte[] zipBytes;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, text) in new[] { ("ffmpeg-master-latest-win64-lgpl/bin/ffprobe.exe", "probe"), ("ffmpeg-master-latest-win64-lgpl/bin/ffmpeg.exe", "the ffmpeg"), ("ffmpeg-master-latest-win64-lgpl/LICENSE.txt", "lgpl") })
                {
                    await using var entry = zip.CreateEntry(name).Open();
                    await entry.WriteAsync(Encoding.UTF8.GetBytes(text));
                }
            }

            zipBytes = stream.ToArray();
        }

        const string baseUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest";
        web.Bytes($"{baseUrl}/ffmpeg-master-latest-win64-lgpl.zip", zipBytes);
        web.Text($"{baseUrl}/checksums.sha256", $"{Sha(zipBytes)} *ffmpeg-master-latest-win64-lgpl.zip\n");
        var m = Manager(web, (_, _, _) => Task.FromResult<string?>("ffmpeg version test"));

        var update = (await m.CheckAsync(ManagedTool.Ffmpeg, default))!;          // not installed: offered
        Assert.Null(update.Current);
        var version = await m.UpdateAsync(update, default);

        Assert.Equal("2026-03-01", version);
        Assert.Equal("the ffmpeg", File.ReadAllText(Path.Combine(User, "ffmpeg", "ffmpeg.exe")));
        Assert.Equal(ToolSource.User, m.SourceOf(ManagedTool.Ffmpeg));
    }

    // ---- a newer program version brings a newer bundled copy ----

    [Fact]
    public async Task An_old_downloaded_copy_does_not_hide_a_newer_bundled_one()
    {
        Put(Path.Combine(App, "tools", "yt-dlp"), "yt-dlp.exe", "bundled");
        var user = Put(Path.Combine(User, "yt-dlp"), "yt-dlp.exe", "downloaded");
        var web = new FakeWeb();
        web.Json(YtApi, """{"tag_name":"2026.09.01"}""");
        var m = Manager(web, (_, exe, _) => Task.FromResult<string?>(File.ReadAllText(exe) == "bundled" ? "2026.10.05" : "2026.08.19"));

        var update = await m.CheckAsync(ManagedTool.YtDlp, default);

        Assert.False(File.Exists(user));                                      // pruned
        Assert.Equal(ToolSource.Bundled, m.SourceOf(ManagedTool.YtDlp));
        Assert.Null(update);                                                  // and the bundled one is already newer than the release
    }
}
