using ShadeNet.Core.Engine;
using ShadeNet.Core.Tools;

namespace ShadeNet.Core.Tests;

/// <summary>
/// Talks to the real GitHub release pages and downloads the real yt-dlp (about 17 MB) into a throw-away folder. Off by default
/// (it needs internet and time): <c>SHADENET_LIVE=1 dotnet test --filter LiveToolTests</c>.
/// </summary>
public class LiveToolTests
{
    static bool Enabled => Environment.GetEnvironmentVariable("SHADENET_LIVE") == "1";

    [Fact]
    public async Task The_real_yt_dlp_release_can_be_found_downloaded_verified_and_started()
    {
        if (!Enabled) return;

        var root = Path.Combine(Path.GetTempPath(), "shadenet-live-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // nothing installed anywhere: the manager offers a download
            var manager = new ToolManager(new EngineSettings(), Path.Combine(root, "user-tools"), Path.Combine(root, "app"), pathVariable: "");
            var offer = await manager.CheckAsync(ManagedTool.YtDlp, default);
            Assert.NotNull(offer);
            Assert.Null(offer!.Current);

            var version = await manager.UpdateAsync(offer, default);

            Assert.Equal(ToolSource.User, manager.SourceOf(ManagedTool.YtDlp));
            Assert.Equal(offer.Latest, version);                                        // the program says the version the release page said
            Assert.Null(await manager.CheckAsync(ManagedTool.YtDlp, default));          // and now it is current
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
