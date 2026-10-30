using Tikai.Core.Attention;
using Tikai.Core.Git;
using Tikai.Core.Reviews;
using Tikai.Review;
using Tikai.Review.Analysis;
using static Tikai.Core.Tests.Reviews.AnalysisTiersTests;

namespace Tikai.Core.Tests.Reviews;

public class AnalysisCleanerTests
{
    private static readonly Guid _first = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid _second = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid _third = Guid.Parse("01900000-0000-7000-8000-000000000003");

    private static readonly AnalysisFile[] _files =
    [
        File("src/Api/Login.cs"),
        File("src/Data/User.cs"),
        File("src/new.cs", oldPath: "src/old.cs"),
        new("src/gone.cs", "src/gone.cs", GitChangeKind.Deleted, 0, 5, new FileFlags(false, false, false)),
        File("tests/LoginTests.cs"),
        File("package-lock.json", isLock: true),
    ];

    private static CleanAnalysis Clean(AnalysisAnswer answer) => AnalysisCleaner.Clean(answer, _files, [_first, _second, _third])!;

    [Fact]
    public void Coverage_keeps_known_criteria_in_ticket_order_with_refs_to_existing_files()
    {
        var clean = Clean(new AnalysisAnswer
        {
            Summary = "  Adds a\nlogin.  ",
            Coverage =
            [
                new() { CriterionId = _second.ToString(), Status = "PARTIAL", Note = "no test", Refs = [new() { Path = "src/Api/Login.cs", LineStart = 20, LineEnd = 10 }] },
                new()
                {
                    CriterionId = _first.ToString(), Status = "covered", Note = "Login checks it",
                    Refs =
                    [
                        new() { Path = "src/Api/Login.cs", LineStart = 3, LineEnd = 9 },
                        new() { Path = "src/Api/Login.cs", LineStart = 3, LineEnd = 9 },
                        new() { Path = "src/old.cs", LineStart = 1 },
                        new() { Path = "src/gone.cs", LineStart = 1, LineEnd = 2 },
                        new() { Path = "nowhere.cs", LineStart = 1, LineEnd = 2 },
                        new() { Path = "package-lock.json", LineStart = 1, LineEnd = 2 },
                        new() { Path = "src/Data/User.cs", LineStart = 0, LineEnd = 2 },
                        null,
                    ],
                },
                new() { CriterionId = _first.ToString(), Status = "missing" },
                new() { CriterionId = Guid.NewGuid().ToString(), Status = "covered" },
                new() { CriterionId = "not a guid", Status = "covered" },
                new() { CriterionId = _third.ToString(), Status = "maybe" },
                null,
            ],
        });

        Assert.Equal("Adds a login.", clean.Summary);
        Assert.Equal([_first, _second], clean.Coverage.Select(c => c.CriterionId));
        var first = clean.Coverage[0];
        Assert.Equal((CriterionCoverage.Covered, "Login checks it"), (first.Status, first.Note));
        Assert.Equal([new CodeRef("src/Api/Login.cs", 3, 9), new CodeRef("src/new.cs", 1, 1)], first.Refs);
        Assert.Equal([new CodeRef("src/Api/Login.cs", 10, 20)], clean.Coverage[1].Refs);
        Assert.Equal(CriterionCoverage.Partial, clean.Coverage[1].Status);
    }

    [Fact]
    public void Every_reviewable_file_lands_in_exactly_one_group_and_the_rest_in_other_changes()
    {
        var clean = Clean(new AnalysisAnswer
        {
            Summary = "x",
            Groups =
            [
                new() { Title = "User table", Layer = "data", Note = "Adds the column.", Files = ["src/Data/User.cs", "nowhere.cs", "package-lock.json"] },
                new() { Title = "", Layer = "api", Files = ["src/gone.cs"] },
                new() { Title = "Login endpoint", Layer = "API", Note = "", Files = ["src/Api/Login.cs", "src/Data/User.cs", "src/old.cs"] },
                new() { Title = "Nothing known", Layer = "ui", Files = ["nowhere.cs"] },
                new() { Title = "Odd layer", Layer = "frontend", Files = ["tests/LoginTests.cs"] },
                null,
            ],
        });

        Assert.Equal(["User table", "Login endpoint", "Odd layer", AnalysisCleaner.OtherTitle], clean.Groups.Select(g => g.Title));
        Assert.Equal(["src/Data/User.cs"], clean.Groups[0].Files);
        Assert.Equal((StoryLayer.Data, "Adds the column."), (clean.Groups[0].Layer, clean.Groups[0].Note));
        Assert.Equal(["src/Api/Login.cs", "src/new.cs"], clean.Groups[1].Files);
        Assert.Equal(StoryLayer.Api, clean.Groups[1].Layer);
        Assert.Equal(StoryLayer.Other, clean.Groups[2].Layer);
        Assert.Equal(["src/gone.cs"], clean.Groups[3].Files);
        Assert.DoesNotContain(clean.Groups, g => g.Files.Contains("package-lock.json"));
    }

    [Fact]
    public void Groups_are_capped_and_the_overflow_goes_to_other_changes()
    {
        var files = Enumerable.Range(1, 20).Select(i => File($"src/f{i:00}.cs")).ToArray();
        var clean = AnalysisCleaner.Clean(
            new AnalysisAnswer { Groups = [.. files.Select(f => new AnswerGroup { Title = f.Path, Layer = "domain", Files = [f.Path] })] },
            files, [])!;

        Assert.Equal(AnalysisCleaner.MaxGroups, clean.Groups.Count);
        Assert.Equal(9, clean.Groups[^1].Files.Count);
    }

    [Fact]
    public void Risks_keep_the_first_entry_per_known_file()
    {
        var clean = Clean(new AnalysisAnswer
        {
            Summary = "x",
            Files =
            [
                new() { Path = "src/Api/Login.cs", Risk = "high", Reason = "Checks passwords." },
                new() { Path = "src/Api/Login.cs", Risk = "low" },
                new() { Path = "src/old.cs", Risk = "Medium", Reason = new string('a', 500) },
                new() { Path = "package-lock.json", Risk = "high" },
                new() { Path = "tests/LoginTests.cs", Risk = "extreme" },
            ],
        });

        Assert.Equal([("src/Api/Login.cs", FileRisk.High), ("src/new.cs", FileRisk.Medium)], clean.Risks.Select(r => (r.Path, r.Risk)));
        Assert.Equal(AnalysisCleaner.MaxNoteLength, clean.Risks[1].Reason.Length);
        Assert.EndsWith("…", clean.Risks[1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_without_summary_coverage_or_groups_is_unusable()
    {
        Assert.Null(AnalysisCleaner.Clean(new AnalysisAnswer { Summary = "  ", Groups = [new() { Title = "x", Files = ["nowhere"] }] }, _files, [_first]));
        Assert.NotNull(AnalysisCleaner.Clean(new AnalysisAnswer { Coverage = [new() { CriterionId = _first.ToString(), Status = "missing" }] }, _files, [_first]));
    }

    [Fact]
    public void Long_texts_are_cut_at_a_word()
    {
        Assert.Equal("one two…", AnalysisCleaner.Text("one two three", 10));
        Assert.Equal("abcdefghi…", AnalysisCleaner.Text("abcdefghijklmnop", 10));
    }
}
