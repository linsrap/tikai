using Tikai.Core.Projects;

namespace Tikai.Core.Tests.Projects;

public class ProjectRulesTests
{
    [Theory]
    [InlineData("hub")]
    [InlineData("tikai-web")]
    [InlineData("a1")]
    [InlineData("project-2026")]
    public void Valid_slugs_pass(string slug)
    {
        Assert.Null(ProjectSlug.ValidationError(slug));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Hub")]
    [InlineData("-hub")]
    [InlineData("hub-")]
    [InlineData("hub--web")]
    [InlineData("hub_web")]
    [InlineData("hub web")]
    [InlineData("../etc")]
    [InlineData("a-very-long-project-slug-that-goes-past-forty")]
    public void Invalid_slugs_are_rejected(string? slug)
    {
        Assert.NotNull(ProjectSlug.ValidationError(slug));
    }

    [Theory]
    [InlineData("HUB")]
    [InlineData("TK")]
    [InlineData("WEB2")]
    [InlineData("ABCDEFGHIJ")]
    public void Valid_keys_pass(string key)
    {
        Assert.Null(ProjectKey.ValidationError(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("H")]
    [InlineData("hub")]
    [InlineData("2HUB")]
    [InlineData("HUB-1")]
    [InlineData("ABCDEFGHIJK")]
    public void Invalid_keys_are_rejected(string? key)
    {
        Assert.NotNull(ProjectKey.ValidationError(key));
    }
}
