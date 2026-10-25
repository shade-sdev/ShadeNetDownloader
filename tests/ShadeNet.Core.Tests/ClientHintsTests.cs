using ShadeNet.Core.Net;

namespace ShadeNet.Core.Tests;

public class ClientHintsTests
{
    const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36";

    static Dictionary<string, string> Hints(string? ua, IReadOnlyDictionary<string, string>? captured = null) =>
        ClientHints.For(ua, captured).ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_chrome_user_agent_gets_the_hints_chrome_sends_with_it()
    {
        var hints = Hints(Chrome);
        Assert.Equal("\"Google Chrome\";v=\"155\", \"Chromium\";v=\"155\", \"Not_A Brand\";v=\"24\"", hints["Sec-CH-UA"]);
        Assert.Equal("?0", hints["Sec-CH-UA-Mobile"]);
        Assert.Equal("\"Windows\"", hints["Sec-CH-UA-Platform"]);
    }

    [Fact]
    public void Edge_and_phones_say_so()
    {
        var edge = Hints(Chrome + " Edg/155.0.0.0");
        Assert.StartsWith("\"Microsoft Edge\";v=\"155\"", edge["Sec-CH-UA"]);

        var phone = Hints("Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Mobile Safari/537.36");
        Assert.Equal("?1", phone["Sec-CH-UA-Mobile"]);
        Assert.Equal("\"Android\"", phone["Sec-CH-UA-Platform"]);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15")]
    [InlineData("curl/8.9")]
    [InlineData(null)]
    public void Browsers_that_send_no_hints_get_none(string? ua) => Assert.Empty(Hints(ua));

    [Fact]
    public void The_browsers_own_hints_win_over_worked_out_ones()
    {
        var captured = new Dictionary<string, string>
        {
            ["sec-ch-ua"] = "\"Brave\";v=\"155\", \"Chromium\";v=\"155\"",
            ["sec-ch-ua-platform"] = "\"Windows\"",
            ["Origin"] = "https://site.example",
        };
        var hints = Hints(Chrome, captured);
        Assert.Equal(2, hints.Count);   // only the hints, and only the ones the browser sent
        Assert.Equal("\"Brave\";v=\"155\", \"Chromium\";v=\"155\"", hints["Sec-CH-UA"]);
    }
}
