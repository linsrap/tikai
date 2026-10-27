using System.Text;
using Tikai.Review;

namespace Tikai.Core.Tests.Reviews;

public class FileClassifierTests
{
    private static readonly FileClassifier _default = new();

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("web/package-lock.json")]
    [InlineData("src/Tikai.Api/packages.lock.json")]
    [InlineData("Cargo.lock")]
    [InlineData("go.sum")]
    public void Lock_files_are_flagged_in_any_directory(string path)
    {
        Assert.Equal(new FileFlags(Lock: true, Generated: false, Large: false), _default.Classify(path, 100, 3, 1));
    }

    [Theory]
    [InlineData("src/Tikai.Data/Migrations/20261027_ReviewBasics.Designer.cs")]
    [InlineData("src/Tikai.Data/Migrations/TikaiDbContextModelSnapshot.cs")]
    [InlineData("src/Foo.g.cs")]
    [InlineData("web/dist/assets/index.js")]
    [InlineData("dist/app.js")]
    [InlineData("lib/jquery.min.js")]
    [InlineData("src/Tikai.Api/obj/project.assets.json")]
    public void Generated_files_are_flagged(string path)
    {
        Assert.True(_default.Classify(path, 100, 1, 1).Generated);
    }

    [Theory]
    [InlineData("src/Tikai.Api/Program.cs")]
    [InlineData("web/src/distance.ts")]
    [InlineData("docs/build-notes.md")]
    [InlineData("README.md")]
    public void Ordinary_files_are_not_flagged(string path)
    {
        Assert.Equal(new FileFlags(false, false, false), _default.Classify(path, 100, 10, 2));
    }

    [Fact]
    public void A_file_over_the_size_or_line_limit_is_large()
    {
        Assert.True(_default.Classify("a.txt", FileClassifier.MaxShownBytes + 1, 1, 0).Large);
        Assert.True(_default.Classify("a.txt", 10, FileClassifier.MaxShownLines, 1).Large);
        Assert.False(_default.Classify("a.txt", FileClassifier.MaxShownBytes, FileClassifier.MaxShownLines, 0).Large);
        Assert.False(_default.Classify("a.png", 10, null, null).Large);
    }

    [Fact]
    public void Gitattributes_add_and_remove_generated_files_last_line_wins()
    {
        var classifier = new FileClassifier("""
            # comment
            web/openapi.json linguist-generated
            src/api/*.d.ts linguist-generated=true
            *.Designer.cs -linguist-generated
            gen/** linguist-generated
            gen/keep.txt linguist-generated=false
            "quoted path" linguist-generated
            """);

        Assert.True(classifier.Classify("web/openapi.json", 1, 1, 1).Generated);
        Assert.True(classifier.Classify("src/api/schema.d.ts", 1, 1, 1).Generated);
        Assert.False(classifier.Classify("src/api/nested/schema.d.ts", 1, 1, 1).Generated);
        Assert.False(classifier.Classify("src/Form.Designer.cs", 1, 1, 1).Generated);
        Assert.True(classifier.Classify("gen/a/b.cs", 1, 1, 1).Generated);
        Assert.False(classifier.Classify("gen/keep.txt", 1, 1, 1).Generated);
        Assert.False(classifier.Classify("other/openapi.json", 1, 1, 1).Generated);
    }

    [Theory]
    [InlineData("src/A.cs", "csharp")]
    [InlineData("web/src/App.tsx", "typescript")]
    [InlineData("compose.yml", "yaml")]
    [InlineData("images/hub/Dockerfile", "dockerfile")]
    [InlineData("src/Tikai.Api/Tikai.Api.csproj", "xml")]
    [InlineData("LICENSE", "plaintext")]
    [InlineData(".env.example", "plaintext")]
    public void Languages_come_from_the_file_name(string path, string language)
    {
        Assert.Equal(language, Languages.For(path));
    }

    [Fact]
    public void Text_is_valid_utf8_without_nul_and_loses_its_bom()
    {
        Assert.Equal("héllo\n", FileText.Decode(Encoding.UTF8.GetBytes("héllo\n")));
        Assert.Equal("x", FileText.Decode([0xEF, 0xBB, 0xBF, (byte)'x']));
        Assert.Equal("", FileText.Decode([]));
        Assert.Null(FileText.Decode([0x89, (byte)'P', (byte)'N', (byte)'G', 0, 1]));
        Assert.Null(FileText.Decode([(byte)'a', 0xFF, (byte)'b']));
    }
}
