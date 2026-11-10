using Tikai.Core.Previews;
using Tikai.Orchestrator.Previews;

namespace Tikai.Core.Tests.Previews;

public class PreviewHostsTests
{
    private static readonly Guid _id = Guid.Parse("01a1c66e-9230-7d0a-a0f8-48750004f1fe");

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public void Without_a_domain_previews_live_under_preview_localhost_for_a_local_hub(string hubHost)
    {
        Assert.Equal("preview.localhost", PreviewHosts.Suffix(null, hubHost));
        Assert.Equal("preview.localhost", PreviewHosts.Suffix("", hubHost));
    }

    [Theory]
    [InlineData("tikai.example.com")]
    [InlineData("192.168.1.20")]
    public void Without_a_domain_a_hub_on_another_host_has_no_previews(string hubHost)
    {
        Assert.Null(PreviewHosts.Suffix(null, hubHost));
    }

    [Fact]
    public void A_configured_domain_is_used_for_any_hub_host_lowercase_and_without_dots_around()
    {
        Assert.Equal("preview.tikai.example.com", PreviewHosts.Suffix(".Preview.Tikai.Example.com.", "tikai.example.com"));
    }

    [Fact]
    public void A_preview_s_url_is_its_id_under_the_suffix_with_the_hub_s_scheme_and_port()
    {
        Assert.Equal("http://01a1c66e92307d0aa0f848750004f1fe.preview.localhost:8080",
            PreviewHosts.Origin(_id, "preview.localhost", "http", 8080));
        Assert.Equal("https://01a1c66e92307d0aa0f848750004f1fe.preview.example.com",
            PreviewHosts.Origin(_id, "preview.example.com", "https", null));
    }

    [Theory]
    [InlineData("01a1c66e92307d0aa0f848750004f1fe.preview.localhost")]
    [InlineData("01A1C66E92307D0AA0F848750004F1FE.Preview.Localhost")]
    public void A_preview_host_names_its_preview(string host)
    {
        Assert.Equal(PreviewHostMatch.Preview, PreviewHosts.Match(host, "preview.localhost", out var id));
        Assert.Equal(_id, id);
    }

    [Theory]
    [InlineData("preview.localhost")]
    [InlineData("x.preview.localhost")]
    [InlineData("01a1c66e-9230-7d0a-a0f8-48750004f1fe.preview.localhost")]
    [InlineData("a.01a1c66e92307d0aa0f848750004f1fe.preview.localhost")]
    public void Other_names_under_the_suffix_are_no_preview_but_still_not_the_hub(string host)
    {
        Assert.Equal(PreviewHostMatch.Unknown, PreviewHosts.Match(host, "preview.localhost", out _));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("tikai.example.com")]
    [InlineData("evilpreview.localhost")]
    public void The_hub_s_own_hosts_are_not_preview_hosts(string host)
    {
        Assert.Equal(PreviewHostMatch.None, PreviewHosts.Match(host, "preview.localhost", out _));
    }

    [Theory]
    [InlineData("tikai.auth=abc; tikai_preview=def; theme=dark", "theme=dark")]
    [InlineData("TIKAI.AUTH=abc;session=1", "session=1")]
    // The app's own cookies, even with names apps share with the hub's script cookie (the hub's are host-only).
    [InlineData("XSRF-TOKEN=x; a=1", "XSRF-TOKEN=x; a=1")]
    [InlineData("tikai.af=x; tikaiX=y; a=1", "a=1")]
    [InlineData("tikai_preview=def", null)]
    [InlineData("a=1;  b=2", "a=1; b=2")]
    public void The_hub_s_cookies_never_reach_the_app(string header, string? expected)
    {
        Assert.Equal(expected, PreviewHosts.StripHubCookies(header));
    }

    private static readonly PreviewAppUpstream[] _apps =
    [
        new("web", "/", "http://web", "c-web"),
        new("api", "/api", "http://api", "c-api"),
        new("v2", "/api/v2", "http://v2", "c-v2"),
    ];

    [Theory]
    [InlineData("/", "web")]
    [InlineData("", "web")]
    [InlineData("/index.html", "web")]
    [InlineData("/api", "api")]
    [InlineData("/api/", "api")]
    [InlineData("/api/users/1", "api")]
    [InlineData("/apix", "web")]
    [InlineData("/API", "web")]
    [InlineData("/api/v2", "v2")]
    [InlineData("/api/v2/x", "v2")]
    [InlineData("/api/v20", "api")]
    [InlineData("/api%2Fv2", "web")]
    public void The_longest_matching_prefix_serves_a_path(string path, string app)
    {
        Assert.Equal(app, PreviewHosts.Route(_apps, path)?.Name);
    }

    [Fact]
    public void The_order_of_the_apps_does_not_matter()
    {
        Assert.Equal("v2", PreviewHosts.Route([.. _apps.Reverse()], "/api/v2/x")?.Name);
    }

    [Fact]
    public void Without_an_app_on_the_root_other_paths_have_no_app()
    {
        Assert.Null(PreviewHosts.Route(_apps[1..], "/"));
        Assert.Null(PreviewHosts.Route([], "/api"));
    }
}
