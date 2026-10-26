using Tikai.Core.Git;

namespace Tikai.Core.Tests.Git;

public class BranchNameAndCredentialTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    [InlineData("release/2026.10")]
    [InlineData("tikai/HUB-42-0192f0c4")]
    public void Valid_branch_names_pass(string name)
    {
        Assert.Null(BranchName.ValidationError(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-main")]
    [InlineData("@")]
    [InlineData("a..b")]
    [InlineData("a b")]
    [InlineData("a~1")]
    [InlineData("a^")]
    [InlineData("a:b")]
    [InlineData("a@{1}")]
    [InlineData("main.lock")]
    [InlineData("main.")]
    [InlineData("/main")]
    [InlineData("main/")]
    [InlineData("a//b")]
    [InlineData(".hidden")]
    [InlineData("a/.hidden")]
    public void Invalid_branch_names_are_rejected(string? name)
    {
        Assert.NotNull(BranchName.ValidationError(name));
    }

    [Fact]
    public void Credentials_never_print_their_secret()
    {
        GitCredential token = new GitCredential.HttpsToken("x-access-token", "ghp_supersecret");
        GitCredential key = new GitCredential.SshKey("-----BEGIN OPENSSH PRIVATE KEY-----\nsecretkey\n");

        Assert.DoesNotContain("supersecret", token.ToString(), StringComparison.Ordinal);
        Assert.Contains("x-access-token", token.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secretkey", key.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("supersecret", $"{new { Credential = token }}", StringComparison.Ordinal);
    }
}
