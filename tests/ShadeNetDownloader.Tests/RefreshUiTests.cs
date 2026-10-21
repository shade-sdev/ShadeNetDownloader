using ShadeNet.Core.Models;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

public class RefreshUiTests
{
    static DownloadItemViewModel Card(DownloadState state, JobErrorKind kind, string? referer)
    {
        var job = new DownloadJob { Url = "https://example.com/dl/a.zip", Referer = referer, FileName = "a.zip" };
        var snapshot = new JobSnapshot(job.Id, state, "a.zip", @"C:\dl", 0, 100, 0, null, 0,
            Array.Empty<SegmentInfo>(), true, state == DownloadState.Failed ? "failed" : null, kind);
        return new DownloadItemViewModel(job, snapshot, null!);
    }

    [Theory]
    [InlineData(JobErrorKind.AddressExpired, true)]
    [InlineData(JobErrorKind.WebPage, true)]
    [InlineData(JobErrorKind.HttpStatus, true)]
    [InlineData(JobErrorKind.Disk, false)]
    [InlineData(JobErrorKind.FileChanged, false)]
    [InlineData(JobErrorKind.Network, false)]
    public void Refresh_address_is_offered_for_links_that_look_used_up(JobErrorKind kind, bool offered) =>
        Assert.Equal(offered, Card(DownloadState.Failed, kind, "https://example.com/page").CanRefreshAddress);

    [Fact]
    public void Refresh_address_needs_a_page_to_look_at_and_a_failed_download()
    {
        Assert.False(Card(DownloadState.Failed, JobErrorKind.AddressExpired, null).CanRefreshAddress);
        Assert.False(Card(DownloadState.Paused, JobErrorKind.None, "https://example.com/page").CanRefreshAddress);
        Assert.False(Card(DownloadState.Completed, JobErrorKind.None, "https://example.com/page").CanRefreshAddress);
        Assert.True(Card(DownloadState.Paused, JobErrorKind.None, "https://example.com/page").HasOwnerPage);
    }

    [Fact]
    public void A_web_page_failure_explains_itself_on_the_card()
    {
        var card = Card(DownloadState.Failed, JobErrorKind.WebPage, "https://example.com/page");
        Assert.Contains("Refresh address", card.HintText);
    }
}
