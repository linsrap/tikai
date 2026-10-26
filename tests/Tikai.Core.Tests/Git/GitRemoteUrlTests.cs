using Tikai.Core.Git;

namespace Tikai.Core.Tests.Git;

public class GitRemoteUrlTests
{
    [Theory]
    [InlineData("https://github.com/linsrap/tikai.git", GitTransport.Https, "github.com")]
    [InlineData("https://gitea.example.org:3000/team/repo", GitTransport.Https, "gitea.example.org")]
    [InlineData("http://localhost:8080/repo.git", GitTransport.Http, "localhost")]
    [InlineData("ssh://git@github.com/linsrap/tikai.git", GitTransport.Ssh, "github.com")]
    [InlineData("ssh://git@127.0.0.1:2222/home/git/repo.git", GitTransport.Ssh, "127.0.0.1")]
    [InlineData("git@github.com:linsrap/tikai.git", GitTransport.Ssh, "github.com")]
    [InlineData("gitlab.example.org:group/repo.git", GitTransport.Ssh, "gitlab.example.org")]
    [InlineData("  https://github.com/linsrap/tikai.git  ", GitTransport.Https, "github.com")]
    public void Accepted_urls(string input, GitTransport transport, string host)
    {
        Assert.True(GitRemoteUrl.TryParse(input, out var url, out var error), error);
        Assert.Equal(transport, url.Transport);
        Assert.Equal(host, url.Host);
        Assert.Equal(input.Trim(), url.Value);
    }

    [Fact]
    public void File_urls_are_recognised_so_the_allowlist_can_refuse_them()
    {
        var path = OperatingSystem.IsWindows() ? "file:///C:/repos/demo.git" : "file:///srv/repos/demo.git";

        var url = GitRemoteUrl.Parse(path);

        Assert.Equal(GitTransport.File, url.Transport);
        Assert.Equal("file", url.Protocol);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/data/repos/other.git")]
    [InlineData("C:/repos/demo.git")]
    [InlineData(@"C:\repos\demo.git")]
    [InlineData("../other.git")]
    [InlineData("ext::sh -c touch% /tmp/pwned")]
    [InlineData("fd::17")]
    [InlineData("-uhelp")]
    [InlineData("--upload-pack=touch /tmp/pwned")]
    [InlineData("git@github.com:-oProxyCommand=x")]
    [InlineData("ftp://example.org/repo.git")]
    [InlineData("https://user:token@github.com/org/repo.git")]
    [InlineData("https://token@github.com/org/repo.git")]
    [InlineData("ssh://git:secret@github.com/org/repo.git")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/org/repo.git\nfoo")]
    [InlineData("file://otherhost/repo.git")]
    public void Rejected_urls(string input)
    {
        Assert.False(GitRemoteUrl.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git", "https://github.com/")]
    [InlineData("https://gitea.example.org:3000/team/repo", "https://gitea.example.org:3000/")]
    [InlineData("ssh://git@github.com/org/repo.git", null)]
    public void The_token_scope_is_the_origin(string input, string? origin)
    {
        Assert.Equal(origin, GitRemoteUrl.Parse(input).HttpOrigin);
    }
}
