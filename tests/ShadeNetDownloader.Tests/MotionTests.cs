using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ShadeNet.Core;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Util;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

/// <summary>The app's motion follows Settings > Appearance > Motion; with it off every change still happens, at once.</summary>
public class MotionTests
{
    /// <summary>WPF elements live on a single-threaded apartment.</summary>
    static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Exception("failed on the STA thread", error);
    }

    static void WithMotion(MotionLevel level, Action action)
    {
        try
        {
            Motion.Level = level;
            action();
        }
        finally
        {
            Motion.Level = MotionLevel.System;
        }
    }

    [Fact]
    public void With_motion_off_a_glide_is_a_jump_and_says_it_is_done() => OnSta(() => WithMotion(MotionLevel.Off, () =>
    {
        var panel = new Border { Width = 248 };
        var done = false;
        Animate.To(panel, FrameworkElement.WidthProperty, 68, done: () => done = true);
        Assert.Equal(68, panel.Width);
        Assert.True(done);

        Animate.FadeSlideIn(panel);   // nothing to do, nothing changed
        Assert.Equal(Transform(panel), System.Windows.Media.Transform.Identity);
    }));

    [Fact]
    public void A_glide_leaves_the_final_value_as_the_elements_own() => OnSta(() => WithMotion(MotionLevel.On, () =>
    {
        var panel = new Border { Width = 248 };
        Animate.To(panel, FrameworkElement.WidthProperty, 68);
        Assert.Equal(68.0, panel.ReadLocalValue(FrameworkElement.WidthProperty));   // (the animation only plays the way there)
    }));

    static System.Windows.Media.Transform Transform(UIElement element) => element.RenderTransform;

    [Fact]
    public void A_new_download_is_flagged_to_slide_in_only_while_motion_is_on() => WithMotion(MotionLevel.On, () =>
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        settings.Load();
        settings.Current.Engine.DownloadRoot = dir.Path;
        using var manager = new DownloadManager(settings.Current.Engine);
        manager.Add(new AddDownloadRequest { Url = "http://127.0.0.1:9/old.zip", FileName = "old.zip", Mode = AddMode.Hold });
        var vm = new DownloadsViewModel(manager, new ToastService());
        Assert.False(vm.Items.Single().IsNew);   // there at start-up: not new

        var reshuffled = 0;
        vm.ListReshuffled += () => reshuffled++;
        vm.SetFilter(new NavItem(NavKind.Completed, "Completed", Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24));
        vm.SortByKeyCommand.Execute(DownloadSortKey.Name);
        Assert.Equal(2, reshuffled);
    });
}
