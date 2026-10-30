using Tikai.Core.Git;
using Tikai.Core.Reviews;
using Tikai.Review;
using Tikai.Review.Analysis;

namespace Tikai.Core.Tests.Reviews;

public class AnalysisTiersTests
{
    internal static AnalysisFile File(string path, int added = 10, int deleted = 0, string? oldPath = null, bool binary = false, bool large = false, bool generated = false, bool isLock = false) =>
        new(path, oldPath, oldPath is null ? GitChangeKind.Modified : GitChangeKind.Renamed, binary ? null : added, binary ? null : deleted,
            new FileFlags(isLock, generated, large));

    [Theory]
    [InlineData("src/Tikai.Api/Auth/AuthEndpoints.cs")]
    [InlineData("src/auth/login.ts")]
    [InlineData("src/Tikai.Api/Security/KeyRing.cs")]
    [InlineData("src/Tikai.Data/Migrations/20261030_ReviewAnalysis.cs")]
    [InlineData("db/schema.sql")]
    [InlineData("images/hub/Dockerfile")]
    [InlineData("compose.yml")]
    [InlineData("web/e2e/compose.e2e.yml")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".gitattributes")]
    public void A_file_in_a_strict_path_makes_the_change_strict(string path)
    {
        var choice = AnalysisTiers.Choose([File("README.md", 1), File(path, 1)]);

        Assert.Equal(AnalysisTier.Strict, choice.Tier);
        Assert.Equal("strict path: " + path, choice.Reason);
    }

    [Fact]
    public void A_rename_out_of_a_strict_path_is_strict()
    {
        Assert.Equal(AnalysisTier.Strict, AnalysisTiers.Choose([File("src/login.cs", oldPath: "src/Auth/login.cs")]).Tier);
    }

    [Fact]
    public void A_large_change_is_strict_and_lock_generated_and_large_files_do_not_count()
    {
        Assert.Equal((AnalysisTier.Strict, "large change: 1501 changed lines"), Tuple(AnalysisTiers.Choose([File("src/a.cs", 1000), File("src/b.cs", 401, 100)])));
        Assert.Equal(AnalysisTier.Normal, AnalysisTiers.Choose(
        [
            File("src/a.cs", 1000, 500),
            File("package-lock.json", 20_000, isLock: true),
            File("src/Foo.g.cs", 9_000, generated: true),
            File("data/big.json", 6_000, large: true),
        ]).Tier);
    }

    [Fact]
    public void A_lock_file_in_a_strict_path_does_not_count()
    {
        Assert.Equal(AnalysisTier.Light, AnalysisTiers.Choose([File("src/Migrations/ModelSnapshot.cs", generated: true), File("docs/a.md")]).Tier);
    }

    [Fact]
    public void A_small_change_to_docs_and_tests_only_is_light()
    {
        var choice = AnalysisTiers.Choose([File("docs/self-hosting.md", 40), File("tests/Tikai.Core.Tests/Reviews/X.cs", 200), File("web/src/a.test.ts", 60), File("package-lock.json", 5_000, isLock: true)]);

        Assert.Equal((AnalysisTier.Light, "small change (300 lines) in docs and tests only"), Tuple(choice));
    }

    [Fact]
    public void Light_needs_every_file_in_a_light_path_and_a_small_size()
    {
        Assert.Equal(AnalysisTier.Normal, AnalysisTiers.Choose([File("docs/a.md", 10), File("src/a.cs", 1)]).Tier);
        Assert.Equal(AnalysisTier.Normal, AnalysisTiers.Choose([File("docs/a.md", 301)]).Tier);
        Assert.Equal(AnalysisTier.Normal, AnalysisTiers.Choose([File("docs/a.md", oldPath: "src/a.cs")]).Tier);
        Assert.Equal((AnalysisTier.Normal, "11 changed lines"), Tuple(AnalysisTiers.Choose([File("src/a.cs", 10, 1)])));
    }

    [Fact]
    public void Only_lock_or_generated_files_is_light()
    {
        Assert.Equal((AnalysisTier.Light, "only lock or generated files"), Tuple(AnalysisTiers.Choose([File("package-lock.json", 9_000, isLock: true)])));
    }

    [Fact]
    public void Globs_match_case_sensitively_like_git()
    {
        Assert.Equal(AnalysisTier.Normal, AnalysisTiers.Choose([File("src/AUTH/x.cs")]).Tier);
    }

    private static (AnalysisTier, string) Tuple(TierChoice choice) => (choice.Tier, choice.Reason);
}
