using ShadeNetDownloader.Services;

namespace ShadeNetDownloader.Tests;

public class AppIdentityTests
{
    [Fact]
    public void The_program_gets_a_taskbar_name_of_its_own_so_the_taskbar_shows_the_window_icon_not_a_shortcuts()
    {
        // (a name that no shortcut carries: a shortcut with the program's path would otherwise decide the button's picture)
        Assert.True(AppIdentity.Set());
        Assert.Equal("ShadeNet.Downloader", AppIdentity.Current());
    }

    [Fact]
    public void The_taskbar_name_is_what_the_program_asks_for()
    {
        Assert.True(AppIdentity.Set("ShadeNet.Downloader"));
        Assert.Equal(AppIdentity.TaskbarId, AppIdentity.Current());
    }
}
