using System.Net;
using System.Security.Cryptography;
using System.Text;
using ShadeNet.Core.Updates;

namespace ShadeNet.Core.Tests;

public class AppUpdateTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "shadenet-upd-" + Guid.NewGuid().ToString("N")[..8]);
    readonly HlsServer _server = new();

    public AppUpdateTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static string Manifest(string version, byte[] package, string name = "ShadeNetDownloader-Update-{v}.zip", string? sha = null, long? size = null) =>
        $$"""{"schema":1,"version":"{{version}}","package":"{{name.Replace("{v}", version)}}","sha256":"{{sha ?? Hash(package)}}","size":{{size ?? package.Length}},"repository":"o/r"}""";

    AppUpdater Updater(string current = "1.0.3") =>
        new("owner/name", Version.Parse(current), Path.Combine(_folder, "updates"), manifestOverride: new Uri(_server.Url("/update.json")));

    (byte[] Package, string Name) Publish(string version, int bytes = 50_000)
    {
        var package = HlsServer.Bytes("pkg" + version, 0, bytes);
        var name = $"ShadeNetDownloader-Update-{version}.zip";
        _server.Put("/update.json", Manifest(version, package));
        _server.Files["/" + name] = package;
        return (package, name);
    }

    [Fact]
    public async Task A_newer_release_is_reported_with_where_its_package_is()
    {
        Publish("1.0.4");
        using var updater = Updater("1.0.3");

        var update = await updater.CheckAsync(default);

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 0, 4), update!.Latest);
        Assert.Equal(new Version(1, 0, 3), update.Current);
        Assert.Equal(_server.Url("/ShadeNetDownloader-Update-1.0.4.zip"), update.PackageUrl.ToString());
    }

    [Theory]
    [InlineData("1.0.4", "1.0.4")]      // the newest: nothing to do
    [InlineData("1.0.4", "1.0.9")]      // a build newer than the newest release (a developer's)
    [InlineData("1.0.4", "1.1.0")]
    [InlineData("1.0.4", "2.0.0")]
    public async Task The_newest_or_a_newer_build_has_nothing_to_update(string released, string running)
    {
        Publish(released);
        using var updater = Updater(running);
        Assert.Null(await updater.CheckAsync(default));
    }

    [Theory]
    [InlineData("1.0.10", "1.0.9", true)]    // numbers, not text: 10 is after 9
    [InlineData("1.1.0", "1.0.99", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("v1.0.5", "1.0.4", true)]    // a leading v is allowed
    [InlineData("1.0.4", "1.0.4.0", false)]  // a fourth part is the same version
    public async Task Versions_are_compared_by_their_numbers(string released, string running, bool newer)
    {
        Publish(released.TrimStart('v'));
        using var updater = Updater(running);
        Assert.Equal(newer, await updater.CheckAsync(default) != null);
    }

    [Fact]
    public async Task No_release_at_all_is_not_an_error()
    {
        using var updater = Updater();   // the server has no update.json: GitHub answers 404 before the first release
        Assert.Null(await updater.CheckAsync(default));
    }

    [Fact]
    public async Task A_server_error_is_reported_as_one()
    {
        _server.Intercept = ctx => { ctx.Response.StatusCode = 503; ctx.Response.Close(); return true; };
        using var updater = Updater();
        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.CheckAsync(default));
        Assert.Contains("503", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"schema":2,"version":"1.0.9","package":"a.zip","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","size":5}""")]                  // a newer kind of manifest
    [InlineData("""{"schema":1,"version":"banana","package":"a.zip","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","size":5}""")]
    [InlineData("""{"schema":1,"version":"1.0.9","package":"..\\..\\evil.zip","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","size":5}""")]          // a path, not a file name
    [InlineData("""{"schema":1,"version":"1.0.9","package":"sub/dir.zip","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","size":5}""")]
    [InlineData("""{"schema":1,"version":"1.0.9","package":"setup.exe","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","size":5}""")]                // not a zip
    [InlineData("""{"schema":1,"version":"1.0.9","package":"a.zip","sha256":"abc","size":5}""")]                                                                                         // not a SHA-256
    [InlineData("""{"schema":1,"version":"1.0.9","package":"a.zip","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","size":0}""")]
    [InlineData("""{"schema":1,"version":"1.0.9","package":"a.zip","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","size":99999999999}""")]
    public async Task An_unreadable_or_hostile_manifest_is_refused(string manifest)
    {
        _server.Put("/update.json", manifest);
        using var updater = Updater();
        await Assert.ThrowsAsync<UpdateException>(() => updater.CheckAsync(default));
    }

    [Fact]
    public void An_update_address_other_than_this_computer_is_not_accepted()
    {
        Assert.Throws<ArgumentException>(() => new AppUpdater("o/r", new Version(1, 0, 0), _folder, manifestOverride: new Uri("https://evil.example/update.json")));
        Assert.Throws<ArgumentException>(() => new AppUpdater("o/r", new Version(1, 0, 0), _folder, manifestOverride: new Uri("http://10.0.0.5/update.json")));
        Assert.Throws<ArgumentException>(() => new AppUpdater("not a repository", new Version(1, 0, 0), _folder));
        using var local = new AppUpdater("o/r", new Version(1, 0, 0), _folder, manifestOverride: new Uri("http://localhost:9/update.json"));
        using var github = new AppUpdater("shade-sdev/ShadeNetDownloader", new Version(1, 0, 0), _folder);
        Assert.Equal("https://github.com/shade-sdev/ShadeNetDownloader/releases/latest/download/update.json", github.ManifestUrl.ToString());
    }

    [Fact]
    public async Task The_package_is_downloaded_verified_and_kept()
    {
        var (package, name) = Publish("1.0.4", bytes: 400_000);
        using var updater = Updater();
        var update = (await updater.CheckAsync(default))!;
        var reported = new List<double>();

        var file = await updater.DownloadAsync(update, new Progress<double>(reported.Add), default);
        await Task.Delay(50);   // Progress<T> posts to the thread pool

        Assert.Equal(Path.Combine(_folder, "updates", "1.0.4", name), file);
        Assert.Equal(package, File.ReadAllBytes(file));
        Assert.False(File.Exists(file + ".part"));
        Assert.Equal(1, reported.Max());
        Assert.True(reported.Count >= 2);
    }

    [Fact]
    public async Task A_package_that_was_tampered_with_is_discarded_and_nothing_stays_behind()
    {
        Publish("1.0.4");
        _server.Files["/ShadeNetDownloader-Update-1.0.4.zip"] = HlsServer.Bytes("evil", 0, 50_000);   // same size, other bytes
        using var updater = Updater();
        var update = (await updater.CheckAsync(default))!;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(update, null, default));

        Assert.Contains("checksum", ex.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "updates"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_package_of_another_size_than_the_release_says_is_refused()
    {
        Publish("1.0.4");
        _server.Files["/ShadeNetDownloader-Update-1.0.4.zip"] = HlsServer.Bytes("short", 0, 10);
        using var updater = Updater();
        var update = (await updater.CheckAsync(default))!;
        await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(update, null, default));

        _server.Files["/ShadeNetDownloader-Update-1.0.4.zip"] = HlsServer.Bytes("long", 0, 5_000_000);
        await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(update, null, default));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "updates"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_missing_package_is_reported_and_a_finished_download_is_not_fetched_twice()
    {
        var (_, name) = Publish("1.0.4");
        _server.Files.TryRemove("/" + name, out _);
        using var updater = Updater();
        var update = (await updater.CheckAsync(default))!;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(update, null, default));
        Assert.Contains("404", ex.Message);

        var (package, _) = Publish("1.0.4");
        var first = await updater.DownloadAsync(update, null, default);
        var requestsSoFar = _server.Hits["/" + name];
        var again = await updater.DownloadAsync(update, null, default);
        Assert.Equal(first, again);
        Assert.Equal(requestsSoFar, _server.Hits["/" + name]);     // the second call used the verified file: no request
        Assert.Equal(package, File.ReadAllBytes(again));
    }

    [Fact]
    public async Task Packages_of_older_checks_are_cleaned_away()
    {
        Publish("1.0.4");
        var stale = Path.Combine(_folder, "updates", "1.0.3");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "old.zip"), "old");
        using var updater = Updater();

        await updater.DownloadAsync((await updater.CheckAsync(default))!, null, default);

        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public async Task A_download_can_be_cancelled_and_leaves_nothing()
    {
        Publish("1.0.4", bytes: 400_000);
        _server.DelayMs = 0;
        using var updater = Updater();
        var update = (await updater.CheckAsync(default))!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.DownloadAsync(update, null, cts.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "updates"), "*.part", SearchOption.AllDirectories));
    }
}
