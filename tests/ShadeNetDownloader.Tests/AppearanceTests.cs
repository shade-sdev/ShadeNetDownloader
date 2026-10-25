using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

/// <summary>Stands in for the theme: records what was applied and answers colours from the look last applied.</summary>
sealed class FakeTheme : IThemeApplier
{
    public bool IsDark { get; set; } = true;
    public Dictionary<string, Color> ThemeColours { get; } = new()
    {
        ["ApplicationBackgroundBrush"] = ThemeColor.Parse("#202020"),
        ["CardBackgroundFillColorDefaultBrush"] = ThemeColor.Parse("#0DFFFFFF"),
        ["TextFillColorPrimaryBrush"] = ThemeColor.Parse("#FFFFFF"),
        ["SystemAccentColor"] = ThemeColor.Parse("#0078D4"),
        ["SystemFillColorSuccessBrush"] = ThemeColor.Parse("#6CCB5F"),
    };
    public List<(AppTheme Theme, AppearanceSettings Look)> Calls { get; } = new();

    public void Apply(AppTheme theme, AppearanceSettings appearance)
    {
        Calls.Add((theme, appearance.Clone()));
        Applied?.Invoke();
    }

    public Color? ColorOf(string key)
    {
        var look = Calls.LastOrDefault().Look;
        if (look != null && look.PaletteFor(IsDark).TryGetValue(key, out var set)) return ThemeColor.Parse(set);
        return ThemeColours.TryGetValue(key, out var c) ? c : null;
    }

    public int Crossfades { get; private set; }

    public void Crossfade() => Crossfades++;

    public List<LayoutSettings> LayoutCalls { get; } = new();

    public void ApplyLayout(LayoutSettings layout)
    {
        LayoutCalls.Add(layout.Clone());
        Applied?.Invoke();
    }

    public event Action? Applied;
}

public class AppearanceTests
{
    // ---- colours as text ----

    [Theory]
    [InlineData("#0078D4", "#0078D4")]
    [InlineData("0078d4", "#0078D4")]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("#800078D4", "#800078D4")]
    [InlineData("#FF0078D4", "#0078D4")]    // fully opaque is written without its alpha
    [InlineData(" #0078D4 ", "#0078D4")]
    public void Colours_are_read_and_written_as_text(string text, string expected)
    {
        Assert.True(ThemeColor.TryParse(text, out var colour));
        Assert.Equal(expected, ThemeColor.Format(colour));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("blue")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void What_is_not_a_colour_is_refused(string? text) => Assert.False(ThemeColor.TryParse(text, out _));

    [Fact]
    public void Contrast_is_measured_as_the_accessibility_guidelines_do()
    {
        Assert.Equal(21, ThemeColor.Contrast(Colors.Black, Colors.White), 1);
        Assert.Equal(1, ThemeColor.Contrast(Colors.Gray, Colors.Gray), 3);
        Assert.True(ThemeColor.Contrast(ThemeColor.Parse("#767676"), Colors.White) >= 4.5);   // the classic just-enough grey
    }

    [Fact]
    public void A_see_through_colour_shows_mixed_with_what_is_under_it()
    {
        Assert.Equal("#808080", ThemeColor.Format(ThemeColor.Over(ThemeColor.Parse("#80FFFFFF"), Colors.Black)));
        Assert.Equal("#123456", ThemeColor.Format(ThemeColor.Over(ThemeColor.Parse("#123456"), Colors.White)));
    }

    [Theory]
    [InlineData("#FF0000")]
    [InlineData("#00FF00")]
    [InlineData("#0078D4")]
    [InlineData("#7C5CFF")]
    [InlineData("#808080")]
    public void Hue_saturation_and_brightness_lead_back_to_the_same_colour(string hex)
    {
        var (h, s, v) = ThemeColor.ToHsv(ThemeColor.Parse(hex));
        Assert.Equal(hex, ThemeColor.Format(ThemeColor.FromHsv(h, s, v)));
    }

    // ---- what a look may hold ----

    [Fact]
    public void A_look_keeps_only_what_can_be_used()
    {
        var look = new AppearanceSettings
        {
            Accent = "not a colour",
            Status = new() { ["done"] = "#00FF00", ["unknown"] = "#FF0000", ["failed"] = "red" },
            Dark = new() { ["textfillcolorprimarybrush"] = "#eeeeee", ["SomethingElse"] = "#000000" },
        };

        Assert.Equal(4, look.Normalize());
        Assert.Null(look.Accent);
        Assert.Equal(new Dictionary<string, string> { ["done"] = "#00FF00" }, look.Status);
        Assert.Equal("#EEEEEE", look.Dark["TextFillColorPrimaryBrush"]);   // the key's own spelling, the colour as written by ShadeNet
        Assert.Single(look.Dark);
    }

    [Fact]
    public void Every_preset_is_a_valid_look_with_its_own_name()
    {
        Assert.Equal(ThemePresets.All.Count, ThemePresets.All.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.NotNull(ThemePresets.Find(ThemePresets.DefaultId));
        foreach (var preset in ThemePresets.All)
        {
            var look = preset.Appearance.Clone();
            Assert.Equal(0, look.Normalize());
            Assert.Equal(preset.Id, look.Preset);
            _ = preset.Swatch;   // its gallery card can be drawn
        }
    }

    [Fact]
    public void Presets_with_their_own_text_colour_are_readable()
    {
        foreach (var preset in ThemePresets.All)
        {
            var dark = preset.Theme != AppTheme.Light;
            var palette = preset.Appearance.PaletteFor(dark);
            if (!palette.ContainsKey("TextFillColorPrimaryBrush")) continue;
            var (_, card, _, text) = preset.Swatch;
            Assert.True(ThemeColor.Contrast(text, card) >= 4.5, $"{preset.Name}: main text on cards");
        }
    }

    [Fact]
    public void The_gradient_themes_are_their_own_grid_and_each_can_be_chosen()
    {
        var (page, theme, settings, current, dir) = Page();
        using var _d = dir;
        Assert.True(page.GradientPresets.Count >= 20);
        Assert.All(page.GradientPresets, p => Assert.True(p.Preset.IsGradient));
        Assert.DoesNotContain(page.Presets, p => p.Preset.IsGradient);
        Assert.Contains(page.GradientPresets, p => p.Preset.Theme == AppTheme.Light);
        Assert.Contains(page.GradientPresets, p => p.Preset.Theme == AppTheme.Dark);
        Assert.All(page.GradientPresets, p => Assert.IsType<LinearGradientBrush>(p.Swatch));

        var mint = page.GradientPresets.Single(p => p.Preset.Id == "mint");
        page.ApplyPresetCommand.Execute(mint);
        Assert.True(mint.IsSelected);
        Assert.Equal(AppTheme.Light, current());
        Assert.Equal("#A8E6CF", theme.Calls.Last().Look.Tint);
        Assert.Equal("#DCEDC1", theme.Calls.Last().Look.Tint2);
        Assert.Equal("Mint Leaf", page.PresetName);
    }

    [Fact]
    public void A_look_is_saved_and_read_back()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        settings.Load();
        settings.Current.Appearance = ThemePresets.Find("nord")!.Appearance.Clone();
        settings.Save();

        var again = new SettingsService(dir.Path);
        again.Load();
        Assert.Equal("nord", again.Current.Appearance.Preset);
        Assert.Equal(BackdropKind.Solid, again.Current.Appearance.Backdrop);
        Assert.Equal("#2E3440", again.Current.Appearance.Dark["applicationbackgroundbrush"]);   // read back with its case-insensitive keys
    }

    // ---- the Appearance page ----

    static (AppearanceSettingsViewModel Page, FakeTheme Theme, SettingsService Settings, Func<AppTheme> Base, TempDir Dir) Page(Func<string?>? pickImage = null,
        Func<string, string?>? pickExport = null, Func<string?>? pickTheme = null)
    {
        var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        settings.Load();
        var theme = new FakeTheme();
        var current = AppTheme.System;
        var page = new AppearanceSettingsViewModel(settings, theme, () => current, t =>
        {
            current = t;
            settings.Current.Theme = t;
        }, pickImage, pickExport, pickTheme);
        return (page, theme, settings, () => current, dir);
    }

    [Fact]
    public void A_preset_brings_its_light_or_dark_and_its_look()
    {
        var (page, theme, settings, current, dir) = Page();
        using var _ = dir;

        page.ApplyPresetCommand.Execute(page.Presets.Single(p => p.Preset.Id == "dracula"));

        Assert.Equal(AppTheme.Dark, current());
        Assert.Equal("dracula", settings.Current.Appearance.Preset);
        Assert.Equal("#BD93F9", theme.Calls.Last().Look.Accent);
        Assert.Equal(AppTheme.Dark, theme.Calls.Last().Theme);
        Assert.True(page.Presets.Single(p => p.Preset.Id == "dracula").IsSelected);
        Assert.Equal("Dracula", page.PresetName);
        Assert.Contains("BD93F9", File.ReadAllText(Path.Combine(dir.Path, "settings.json")));
    }

    [Fact]
    public void Changing_a_preset_makes_it_custom_and_changing_it_back_makes_it_the_preset_again()
    {
        var (page, _, settings, _, dir) = Page();
        using var _d = dir;
        page.ApplyPresetCommand.Execute(page.Presets.Single(p => p.Preset.Id == "midnight"));

        page.AccentColor = "#FF0000";
        Assert.Equal(ThemePresets.CustomId, settings.Current.Appearance.Preset);
        Assert.Equal("Custom", page.PresetName);
        Assert.DoesNotContain(page.Presets, p => p.IsSelected);

        page.AccentColor = "#7C5CFF";
        Assert.Equal("midnight", settings.Current.Appearance.Preset);
    }

    [Fact]
    public void The_accent_follows_windows_until_a_colour_is_chosen()
    {
        var (page, theme, settings, _, dir) = Page();
        using var _d = dir;
        Assert.True(page.UseWindowsAccent);
        Assert.Equal("#0078D4", page.AccentColor);   // the Windows colour, shown

        page.AccentColor = "#22C55E";
        Assert.False(page.UseWindowsAccent);
        Assert.Equal("#22C55E", theme.Calls.Last().Look.Accent);

        page.UseWindowsAccent = true;
        Assert.Null(settings.Current.Appearance.Accent);
        page.UseWindowsAccent = false;   // a colour of one's own starts from the one showing
        Assert.Equal("#0078D4", settings.Current.Appearance.Accent);
    }

    [Fact]
    public void The_window_material_is_one_of_four()
    {
        var (page, theme, _, _, dir) = Page();
        using var _d = dir;
        Assert.True(page.IsMica);
        page.IsSolid = true;
        Assert.False(page.IsMica);
        Assert.Equal(BackdropKind.Solid, theme.Calls.Last().Look.Backdrop);
    }

    [Fact]
    public void A_palette_colour_is_set_for_the_theme_showing_and_can_be_reset()
    {
        var (page, theme, settings, _, dir) = Page();
        using var _d = dir;
        var cards = page.PaletteGroups.SelectMany(g => g.Items).Single(i => i.Key == "CardBackgroundFillColorDefaultBrush");
        Assert.False(cards.IsSet);
        Assert.Equal("#0DFFFFFF", cards.Value);   // the theme's own, see-through

        cards.Value = "#33112233";
        Assert.True(cards.IsSet);
        Assert.Equal("#33112233", settings.Current.Appearance.Dark["CardBackgroundFillColorDefaultBrush"]);
        Assert.Empty(settings.Current.Appearance.Light);

        theme.IsDark = false;   // the light theme keeps its own
        page.Refresh();
        Assert.False(cards.IsSet);
        Assert.Contains("light", page.PaletteTitle);

        theme.IsDark = true;
        page.Refresh();
        cards.ResetCommand.Execute(null);
        Assert.Empty(settings.Current.Appearance.Dark);
    }

    [Fact]
    public void State_colours_are_solid_and_reset_together()
    {
        var (page, theme, settings, _, dir) = Page();
        using var _d = dir;
        var done = page.StatusItems.Single(i => i.Key == StatusColors.Done);
        Assert.Equal("#6CCB5F", done.Value);

        done.Value = "#80EC4899";   // a see-through colour is made solid
        Assert.Equal("#EC4899", settings.Current.Appearance.Status[StatusColors.Done]);

        page.ResetStatusCommand.Execute(null);
        Assert.Empty(settings.Current.Appearance.Status);
        Assert.Empty(theme.Calls.Last().Look.Status);
    }

    [Fact]
    public void Hard_to_read_text_is_pointed_out()
    {
        var (page, _, _, _, dir) = Page();
        using var _d = dir;
        Assert.Null(page.ContrastWarning);

        var text = page.PaletteGroups.SelectMany(g => g.Items).Single(i => i.Key == "TextFillColorPrimaryBrush");
        text.Value = "#3A3A3A";   // dark grey on a dark card
        Assert.NotNull(page.ContrastWarning);

        text.ResetCommand.Execute(null);
        Assert.Null(page.ContrastWarning);
    }

    [Fact]
    public void Back_to_the_windows_look_undoes_everything()
    {
        var (page, _, settings, current, dir) = Page();
        using var _d = dir;
        page.ApplyPresetCommand.Execute(page.Presets.Single(p => p.Preset.Id == "nord"));
        page.ResetAllCommand.Execute(null);

        Assert.Equal(AppTheme.System, current());
        Assert.Null(settings.Current.Appearance.Accent);
        Assert.Equal(BackdropKind.Mica, settings.Current.Appearance.Backdrop);
        Assert.Empty(settings.Current.Appearance.Dark);
        Assert.Equal(ThemePresets.DefaultId, settings.Current.Appearance.Preset);
    }

    // ---- stage 2: a tint or gradient, and a picture ----

    [Fact]
    public void Tint_and_picture_values_are_kept_in_range()
    {
        var look = new AppearanceSettings
        {
            Tint2 = "#FF0000",          // a gradient without its first colour
            TintAngle = -90,
            TintStrength = 7,
            ImageBlur = 500,
            ImageDim = double.NaN,
            Image = "  ",
        };
        look.Normalize();
        Assert.Null(look.Tint2);
        Assert.Equal(270, look.TintAngle);
        Assert.Equal(1, look.TintStrength);
        Assert.Equal(AppearanceSettings.MaxBlur, look.ImageBlur);
        Assert.Equal(0.4, look.ImageDim);
        Assert.Null(look.Image);
    }

    [Fact]
    public void A_tint_starts_from_the_accent_and_a_gradient_adds_a_second_colour()
    {
        var (page, theme, settings, _, dir) = Page();
        using var _d = dir;
        Assert.False(page.HasTint);

        page.HasTint = true;
        Assert.Equal("#0078D4", settings.Current.Appearance.Tint);   // the accent showing
        page.UseGradient = true;
        Assert.NotNull(settings.Current.Appearance.Tint2);
        Assert.NotEqual(settings.Current.Appearance.Tint, settings.Current.Appearance.Tint2);

        page.TintStrength = 40;
        page.TintAngle = 90;
        Assert.Equal(0.4, theme.Calls.Last().Look.TintStrength, 3);
        Assert.Equal(90, theme.Calls.Last().Look.TintAngle);

        page.HasTint = false;   // no tint, no gradient
        Assert.Null(settings.Current.Appearance.Tint);
        Assert.Null(settings.Current.Appearance.Tint2);
    }

    [Fact]
    public void A_gradient_preset_is_recognised_again_when_its_look_comes_back()
    {
        var (page, _, settings, _, dir) = Page();
        using var _d = dir;
        page.ApplyPresetCommand.Execute(page.GradientPresets.Single(p => p.Preset.Id == "aurora"));
        Assert.IsType<LinearGradientBrush>(page.GradientPresets.Single(p => p.Preset.Id == "aurora").Background);

        page.TintStrength = 50;
        Assert.Equal(ThemePresets.CustomId, settings.Current.Appearance.Preset);
        page.TintStrength = 28;
        Assert.Equal("aurora", settings.Current.Appearance.Preset);
    }

    static string Picture(string folder, string name, byte shade = 0x80)
    {
        var pixels = Enumerable.Repeat(shade, 2 * 2 * 4).ToArray();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 2 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(folder, name);
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    [Fact]
    public void A_picture_is_kept_as_a_copy_of_its_own_and_old_copies_are_cleaned_up()
    {
        using var source = new TempDir();
        var next = Picture(source.Path, "holiday.png");
        var (page, theme, settings, _, dir) = Page(() => next);
        using var _d = dir;

        page.ChooseImageCommand.Execute(null);
        var kept = settings.Current.Appearance.Image!;
        Assert.StartsWith(Path.Combine(dir.Path, "backgrounds"), kept);
        Assert.True(File.Exists(kept));
        Assert.True(page.HasImage);
        Assert.Equal(kept, theme.Calls.Last().Look.Image);

        File.Delete(next);   // the original can go: the look keeps working
        Assert.True(File.Exists(kept));

        next = Picture(source.Path, "mountains.png", 0x20);
        page.ChooseImageCommand.Execute(null);
        Assert.NotEqual(kept, settings.Current.Appearance.Image);
        Assert.False(File.Exists(kept));   // the copy no longer used is deleted when the look is saved

        page.RemoveImageCommand.Execute(null);
        Assert.False(page.HasImage);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "backgrounds")));
    }

    [Fact]
    public void A_file_that_is_no_picture_is_refused_with_the_reason()
    {
        using var source = new TempDir();
        var text = Path.Combine(source.Path, "notes.png");
        File.WriteAllText(text, "not a picture");
        var (page, _, settings, _, dir) = Page(() => text);
        using var _d = dir;

        page.ChooseImageCommand.Execute(null);
        Assert.Null(settings.Current.Appearance.Image);
        Assert.NotNull(page.ImageError);
    }

    [Fact]
    public void The_layer_behind_the_window_shows_the_tint_and_the_picture()
    {
        using var source = new TempDir();
        var layer = new BackgroundLayer();
        layer.Update(new AppearanceSettings { Tint = "#FF0000", Tint2 = "#0000FF", TintStrength = 0.3, Image = Picture(source.Path, "a.png"), ImageBlur = 10, ImageDim = 0.5 });

        Assert.True(layer.HasTint);
        Assert.IsType<LinearGradientBrush>(layer.Tint);
        Assert.Equal(0.3, layer.TintOpacity);
        Assert.True(layer.HasImage);
        Assert.Equal(-20, layer.BlurMargin.Left);
        Assert.Equal(0.5, layer.Dim);

        layer.Update(new AppearanceSettings { Image = Path.Combine(source.Path, "gone.png") });
        Assert.False(layer.HasTint);
        Assert.False(layer.HasImage);
        Assert.NotNull(layer.ImageError);
    }

    // ---- stage 3: text and layout ----

    [Fact]
    public void Layout_values_are_kept_in_range()
    {
        var layout = new LayoutSettings { Font = "  Cascadia Code ", Zoom = 3, Density = (ListDensity)42, Corners = (Roundness)(-1), Motion = (MotionLevel)9 };
        layout.Normalize();
        Assert.Equal("Cascadia Code", layout.Font);
        Assert.Equal(LayoutSettings.MaxZoom, layout.Zoom);
        Assert.Equal(ListDensity.Comfortable, layout.Density);
        Assert.Equal(Roundness.Default, layout.Corners);
        Assert.Equal(MotionLevel.System, layout.Motion);

        layout.Zoom = 1.123;
        layout.Normalize();
        Assert.Equal(1.1, layout.Zoom, 3);   // steps of 5 %
        layout.Font = " ";
        layout.Normalize();
        Assert.Null(layout.Font);
    }

    [Fact]
    public void Size_density_corners_and_motion_are_applied_and_remembered()
    {
        var (page, theme, settings, _, dir) = Page();
        using var _d = dir;

        page.ZoomPercent = 127;   // to the nearest 5 %
        Assert.Equal(125, page.ZoomPercent);
        Assert.Equal(1.25, theme.LayoutCalls.Last().Zoom, 3);

        page.IsCompact = true;
        page.IsSquare = true;
        page.IsMotionOff = true;
        Assert.Equal(ListDensity.Compact, theme.LayoutCalls.Last().Density);
        Assert.Equal(0, theme.LayoutCalls.Last().CornerFactor);
        Assert.Equal(MotionLevel.Off, theme.LayoutCalls.Last().Motion);
        Assert.True(page.IsCompact && page.IsSquare && page.IsMotionOff);

        var reloaded = new SettingsService(dir.Path);
        reloaded.Load();
        Assert.Equal(1.25, reloaded.Current.Layout.Zoom, 3);
        Assert.Equal(ListDensity.Compact, reloaded.Current.Layout.Density);

        page.ResetLayoutCommand.Execute(null);
        Assert.Equal(100, page.ZoomPercent);
        Assert.True(page.IsComfortable && page.IsDefaultCorners && page.IsMotionSystem);
    }

    [Fact]
    public void The_name_in_the_title_bar_is_plain_unless_chosen_otherwise()
    {
        var (page, theme, settings, _, dir) = Page();
        using var _d = dir;
        Assert.True(page.IsWordmarkPlain);

        page.IsWordmarkGradient = true;
        Assert.Equal(WordmarkStyle.Gradient, theme.LayoutCalls.Last().Wordmark);
        page.IsWordmarkAccent = true;
        Assert.Equal(WordmarkStyle.Accent, settings.Current.Layout.Wordmark);

        var odd = new LayoutSettings { Wordmark = (WordmarkStyle)7 };
        odd.Normalize();
        Assert.Equal(WordmarkStyle.Plain, odd.Wordmark);
    }

    [Fact]
    public void A_preset_never_changes_text_and_layout_and_layout_never_makes_a_look_custom()
    {
        var (page, _, settings, _, dir) = Page();
        using var _d = dir;
        page.IsSpacious = true;
        page.ZoomPercent = 110;

        page.ApplyPresetCommand.Execute(page.Presets.Single(p => p.Preset.Id == "nord"));
        Assert.Equal(ListDensity.Spacious, settings.Current.Layout.Density);
        Assert.Equal(1.1, settings.Current.Layout.Zoom, 3);

        page.IsRound = true;
        Assert.Equal("nord", settings.Current.Appearance.Preset);
    }

    [Fact]
    public void The_windows_font_comes_first_and_choosing_it_clears_the_font()
    {
        var (page, _, settings, _, dir) = Page();
        using var _d = dir;
        Assert.Equal(AppearanceSettingsViewModel.WindowsFont, page.FontChoices[0]);
        Assert.Equal(AppearanceSettingsViewModel.WindowsFont, page.SelectedFont);

        page.SelectedFont = "Consolas";
        Assert.Equal("Consolas", settings.Current.Layout.Font);
        page.SelectedFont = AppearanceSettingsViewModel.WindowsFont;
        Assert.Null(settings.Current.Layout.Font);
    }

    [Fact]
    public void Motion_follows_the_choice()
    {
        try
        {
            AppThemeService.ApplyMotion(MotionLevel.Off);
            Assert.False(Motion.Allowed);
            AppThemeService.ApplyMotion(MotionLevel.On);
            Assert.True(Motion.Allowed);
        }
        finally
        {
            AppThemeService.ApplyMotion(MotionLevel.System);
        }
    }

    // ---- stage 4: themes as files ----

    [Fact]
    public void A_theme_file_carries_the_look_and_its_picture_but_no_path()
    {
        using var source = new TempDir();
        var look = ThemePresets.Find("dracula")!.Appearance.Clone();
        look.Tint = "#FF79C6";
        look.Image = Picture(source.Path, "bg.jpg");

        var text = ThemeFile.Write("Night", AppTheme.Dark, look, maxImageBytes: 1_000_000);
        Assert.DoesNotContain(source.Path.Replace("\\", "\\\\"), text);   // no path from this computer

        var read = ThemeFile.Parse(text);
        Assert.Equal("Night", read.Name);
        Assert.Equal(AppTheme.Dark, read.Theme);
        Assert.Equal("#BD93F9", read.Appearance.Accent);
        Assert.Equal("#FF79C6", read.Appearance.Tint);
        Assert.Equal("#282A36", read.Appearance.Dark["ApplicationBackgroundBrush"]);
        Assert.Null(read.Appearance.Image);
        Assert.Equal(File.ReadAllBytes(look.Image), read.Image);
        Assert.Equal(".jpg", read.ImageExtension);
        Assert.Equal(0, read.Dropped);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"format\": \"something-else\", \"version\": 1, \"appearance\": {} }")]
    [InlineData("{ \"format\": \"shadenet-theme\", \"version\": 1 }")]
    [InlineData("[1, 2, 3]")]
    public void What_is_not_a_theme_is_refused(string text) => Assert.Throws<ThemeFileException>(() => ThemeFile.Parse(text));

    [Fact]
    public void A_theme_from_a_newer_shadenet_says_so()
    {
        var ex = Assert.Throws<ThemeFileException>(() => ThemeFile.Parse("{ \"format\": \"shadenet-theme\", \"version\": 99, \"appearance\": {} }"));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void What_a_theme_cannot_use_is_left_out_and_counted()
    {
        var read = ThemeFile.Parse("""
            {
              "format": "shadenet-theme", "version": 1, "name": "Odd", "theme": "Light",
              "appearance": { "accent": "#12345", "image": "C:\\Windows\\secret.png",
                              "light": { "TextFillColorPrimaryBrush": "#111111", "NotAKey": "#000000" } },
              "image": { "extension": ".exe", "data": "%%% not base64 %%%" }
            }
            """);
        Assert.Equal(3, read.Dropped);              // the accent, the unknown key, the picture
        Assert.Null(read.Appearance.Accent);
        Assert.Null(read.Appearance.Image);        // a path in the file is never used
        Assert.Null(read.Image);
        Assert.Equal("#111111", read.Appearance.Light["TextFillColorPrimaryBrush"]);
        Assert.Equal(ThemePresets.CustomId, read.Appearance.Preset);
    }

    [Fact]
    public void A_shared_theme_is_tried_out_and_can_be_undone()
    {
        using var files = new TempDir();
        var themePath = Path.Combine(files.Path, "night" + ThemeFile.Extension);
        var shared = ThemePresets.Find("nord")!.Appearance.Clone();
        shared.Image = Picture(files.Path, "wall.png", 0x40);
        File.WriteAllText(themePath, ThemeFile.Write("Night", AppTheme.Dark, shared, 1_000_000));

        string? export = null;
        var (page, theme, settings, current, dir) = Page(pickExport: name => export = Path.Combine(files.Path, name + ".out"), pickTheme: () => themePath);
        using var _d = dir;
        page.ApplyPresetCommand.Execute(page.Presets.Single(p => p.Preset.Id == "rose"));   // the look before: light, rose
        Assert.Equal(AppTheme.Light, current());

        page.ImportThemeCommand.Execute(null);
        Assert.True(page.IsPreviewing);
        Assert.Equal("Night", page.PreviewName);
        Assert.Equal(AppTheme.Dark, current());
        Assert.Equal("#88C0D0", settings.Current.Appearance.Accent);
        Assert.Equal("#88C0D0", theme.Calls.Last().Look.Accent);
        var picture = settings.Current.Appearance.Image!;
        Assert.True(File.Exists(picture));
        Assert.StartsWith(Path.Combine(dir.Path, "backgrounds"), picture);

        page.UndoImportCommand.Execute(null);
        Assert.False(page.IsPreviewing);
        Assert.Equal(AppTheme.Light, current());
        Assert.Equal("rose", settings.Current.Appearance.Preset);
        Assert.Null(settings.Current.Appearance.Image);

        page.ImportThemeCommand.Execute(null);
        page.KeepImportedCommand.Execute(null);
        Assert.False(page.IsPreviewing);
        Assert.Equal("#88C0D0", settings.Current.Appearance.Accent);

        page.ExportThemeCommand.Execute(null);   // and it can be shared on
        Assert.NotNull(export);
        Assert.Equal("#88C0D0", ThemeFile.Read(export!).Appearance.Accent);
        Assert.NotNull(ThemeFile.Read(export!).Image);
    }

    [Fact]
    public void Changing_anything_while_trying_a_theme_keeps_it()
    {
        using var files = new TempDir();
        var themePath = Path.Combine(files.Path, "x" + ThemeFile.Extension);
        File.WriteAllText(themePath, ThemeFile.Write("X", AppTheme.Dark, ThemePresets.Find("midnight")!.Appearance, 0));
        var (page, _, settings, _, dir) = Page(pickTheme: () => themePath);
        using var _d = dir;

        page.ImportThemeCommand.Execute(null);
        Assert.True(page.IsPreviewing);
        page.IsSolid = true;
        Assert.False(page.IsPreviewing);
        Assert.Equal(BackdropKind.Solid, settings.Current.Appearance.Backdrop);
    }

    [Fact]
    public void A_file_that_is_not_a_theme_changes_nothing_and_says_why()
    {
        using var files = new TempDir();
        var path = Path.Combine(files.Path, "notes" + ThemeFile.Extension);
        File.WriteAllText(path, "hello");
        var (page, _, settings, _, dir) = Page(pickTheme: () => path);
        using var _d = dir;

        page.ImportThemeCommand.Execute(null);
        Assert.False(page.IsPreviewing);
        Assert.NotNull(page.ThemeFileMessage);
        Assert.Equal(ThemePresets.DefaultId, settings.Current.Appearance.Preset);
    }

    // ---- app icons ----

    sealed class FakeIcons : IAppIconApplier
    {
        public IReadOnlyList<AppIconChoice> Choices { get; } = new[]
        {
            new AppIconChoice(AppIconService.DefaultId, "ShadeNet", "app.ico", "app.png"),
            new AppIconChoice("sunset", "Sunset", "sunset.ico", "sunset.png"),
        };

        public List<string> Applied { get; } = new();
        public void Apply(string id) => Applied.Add(id);
    }

    [Fact]
    public void The_app_icons_are_shadenets_own_and_those_listed_whose_files_are_there()
    {
        using var dir = new TempDir();
        var icons = Path.Combine(dir.Path, "Icons");
        Directory.CreateDirectory(icons);
        File.WriteAllText(Path.Combine(dir.Path, "app.ico"), "");
        File.WriteAllText(Path.Combine(icons, "icons.json"),
            "[ {\"id\": \"sunset\", \"name\": \"Sunset\"}, {\"id\": \"gone\", \"name\": \"Gone\"}, {\"id\": \"../evil\", \"name\": \"Evil\"} ]");
        File.WriteAllText(Path.Combine(icons, "sunset.ico"), "");
        File.WriteAllText(Path.Combine(icons, "sunset.png"), "");

        var service = new AppIconService(folder: dir.Path);
        Assert.Equal(new[] { AppIconService.DefaultId, "sunset" }, service.Choices.Select(c => c.Id));
        Assert.Equal("Sunset", service.Choices[1].Name);

        service.Apply("SUNSET");
        Assert.Equal("sunset", service.Current);
        service.Apply("no-such-icon");   // an unknown one is ShadeNet's own
        Assert.Equal(AppIconService.DefaultId, service.Current);
    }

    [Fact]
    public void Without_a_list_there_is_only_shadenets_own_icon()
    {
        using var dir = new TempDir();
        Assert.Single(new AppIconService(folder: dir.Path).Choices);
    }

    [Fact]
    public void An_app_icon_is_chosen_shown_and_remembered_and_a_preset_never_changes_it()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        settings.Load();
        var icons = new FakeIcons();
        var current = AppTheme.System;
        var page = new AppearanceSettingsViewModel(settings, new FakeTheme(), () => current, t => current = t, icons: icons);
        Assert.Equal(2, page.AppIcons.Count);
        Assert.True(page.AppIcons[0].IsSelected);

        page.ChooseAppIconCommand.Execute(page.AppIcons[1]);
        Assert.Equal(new[] { "sunset" }, icons.Applied);
        Assert.True(page.AppIcons[1].IsSelected);
        Assert.False(page.AppIcons[0].IsSelected);

        page.ApplyPresetCommand.Execute(page.Presets.Single(p => p.Preset.Id == "nord"));
        page.ResetAllCommand.Execute(null);
        Assert.Equal("sunset", settings.Current.AppIcon);

        var reloaded = new SettingsService(dir.Path);
        reloaded.Load();
        Assert.Equal("sunset", reloaded.Current.AppIcon);
    }
}
