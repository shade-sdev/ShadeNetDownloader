using System.IO;
using ShadeNet.Core;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Util;
using ShadeNetDownloader.ViewModels;
using Wpf.Ui;

namespace ShadeNetDownloader.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shadenet-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch
        {
            // sqlite may still hold a file
        }
    }
}

public class SettingsServiceTests
{
    [Fact]
    public void Default_settings_can_be_saved_and_loaded_back()
    {
        // Regression: an unset window position used to be NaN, which JSON cannot represent - every save crashed.
        using var dir = new TempDir();
        var a = new SettingsService(dir.Path);
        a.Load();
        a.Save();

        var b = new SettingsService(dir.Path);
        var loaded = b.Load();
        Assert.Equal(AppTheme.System, loaded.Theme);
        Assert.Null(loaded.Window.Left);
        Assert.Null(loaded.Window.Top);
        Assert.Equal(1180, loaded.Window.Width);
    }

    [Fact]
    public void Everything_the_user_changes_survives_a_restart()
    {
        using var dir = new TempDir();
        var a = new SettingsService(dir.Path);
        var s = a.Load();
        s.Theme = AppTheme.Dark;
        s.Window.Left = 120;
        s.Window.Top = 80;
        s.Window.Maximized = true;
        s.Engine.DefaultConnections = 12;
        s.Engine.MaxConcurrentJobs = 5;
        s.Engine.DownloadRoot = @"D:\Stuff";
        s.Engine.RampUpInterval = TimeSpan.FromSeconds(3);
        s.Engine.HostConnectionLimits["strict.example"] = 2;
        a.Save();

        var loaded = new SettingsService(dir.Path).Load();
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(120, loaded.Window.Left);
        Assert.True(loaded.Window.Maximized);
        Assert.Equal(12, loaded.Engine.DefaultConnections);
        Assert.Equal(5, loaded.Engine.MaxConcurrentJobs);
        Assert.Equal(@"D:\Stuff", loaded.Engine.DownloadRoot);
        Assert.Equal(TimeSpan.FromSeconds(3), loaded.Engine.RampUpInterval);
        Assert.Equal(2, loaded.Engine.HostConnectionLimits["STRICT.example"]); // lookup is case-insensitive again
    }

    [Fact]
    public void Theme_is_stored_as_readable_text()
    {
        using var dir = new TempDir();
        var a = new SettingsService(dir.Path);
        a.Load().Theme = AppTheme.Light;
        a.Save();
        Assert.Contains("\"Light\"", File.ReadAllText(a.SettingsPath));
    }

    [Fact]
    public void A_damaged_file_falls_back_to_defaults_and_is_kept_as_bad()
    {
        using var dir = new TempDir();
        var a = new SettingsService(dir.Path);
        File.WriteAllText(a.SettingsPath, "{ this is not json");

        var loaded = a.Load();
        Assert.Equal(AppTheme.System, loaded.Theme);
        Assert.True(File.Exists(a.SettingsPath + ".bad"));
    }

    [Fact]
    public void Saving_never_leaves_a_temp_file_behind_and_replaces_the_old_one()
    {
        using var dir = new TempDir();
        var a = new SettingsService(dir.Path);
        a.Load();
        for (var i = 0; i < 5; i++)
        {
            a.Current.Engine.DefaultConnections = i + 1;
            a.Save();
        }

        Assert.False(File.Exists(a.SettingsPath + ".tmp"));
        Assert.Equal(5, new SettingsService(dir.Path).Load().Engine.DefaultConnections);
    }

    [Fact]
    public void The_data_folder_is_created_and_paths_live_inside_it()
    {
        using var dir = new TempDir();
        var nested = System.IO.Path.Combine(dir.Path, "a", "b");
        var s = new SettingsService(nested);
        Assert.True(Directory.Exists(nested));
        Assert.StartsWith(nested, s.SettingsPath);
        Assert.StartsWith(nested, s.DatabasePath);
    }
}

public class ByteFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(10 * 1024, "10.0 KB")]
    [InlineData(150 * 1024, "150 KB")]
    [InlineData(5L * 1024 * 1024, "5.00 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    [InlineData(-5, "0 B")]
    public void Sizes_use_binary_units_and_sensible_precision(long bytes, string expected) =>
        Assert.Equal(expected.Replace('.', System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0]),
            ByteFormat.Size(bytes));

    [Fact]
    public void Speed_appends_per_second() => Assert.EndsWith("/s", ByteFormat.Speed(2048));

    [Theory]
    [InlineData(null, "--")]
    [InlineData(0.2, "<1 s")]
    [InlineData(45.0, "45 s")]
    [InlineData(125.0, "2 min 5 s")]
    [InlineData(7500.0, "2 h 5 min")]
    [InlineData(200000.0, "2 d 7 h")]
    public void Eta_is_short_and_human(double? seconds, string expected) =>
        Assert.Equal(expected, ByteFormat.Eta(seconds is { } s ? TimeSpan.FromSeconds(s) : null));
}
