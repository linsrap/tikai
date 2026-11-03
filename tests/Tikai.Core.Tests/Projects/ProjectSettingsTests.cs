using Tikai.Core.Projects;
using Tikai.Core.Reviews;

namespace Tikai.Core.Tests.Projects;

public class ProjectSettingsTests
{
    [Fact]
    public void Empty_settings_use_the_default_model_per_tier()
    {
        var settings = ProjectSettings.Parse("{}");

        Assert.Equal("claude-haiku-5-5", settings.AnalysisModel(AnalysisTier.Light));
        Assert.Equal("claude-sonnet-5-5", settings.AnalysisModel(AnalysisTier.Normal));
        Assert.Equal("claude-opus-5-5", settings.AnalysisModel(AnalysisTier.Strict));
    }

    [Fact]
    public void A_set_model_wins_and_round_trips_as_snake_case_json()
    {
        var json = new ProjectSettings(new ReviewSettings(new ReviewModels(Strict: "claude-fable-5-1"))).ToJson();

        Assert.Equal("""{"review":{"models":{"light":null,"normal":null,"strict":"claude-fable-5-1"}}}""", json);
        var settings = ProjectSettings.Parse(json);
        Assert.Equal("claude-fable-5-1", settings.AnalysisModel(AnalysisTier.Strict));
        Assert.Equal("claude-sonnet-5-5", settings.AnalysisModel(AnalysisTier.Normal));
    }

    [Fact]
    public void Delivery_defaults_to_squash_merges_and_stores_its_choices_as_snake_case()
    {
        Assert.Equal(new DeliverySettings(DeliveryMode.Merge, MergeStyle.Squash), ProjectSettings.Parse("{}").DeliveryOrDefault);

        var json = new ProjectSettings(Delivery: new DeliverySettings(DeliveryMode.PushBranch)).ToJson();

        Assert.Equal("""{"review":null,"delivery":{"mode":"push_branch","merge_style":null}}""", json);
        Assert.Equal(new DeliverySettings(DeliveryMode.PushBranch, MergeStyle.Squash), ProjectSettings.Parse(json).DeliveryOrDefault);
        Assert.Equal(MergeStyle.MergeCommit, ProjectSettings.Parse("""{"delivery":{"merge_style":"merge_commit"}}""").DeliveryOrDefault.MergeStyle);
    }

    [Fact]
    public void Model_ids_are_checked_for_form()
    {
        Assert.Equal([("normal", "Enter a model id such as \"claude-opus-5-5\".")], new ReviewModels("claude-haiku-5-5", "has space").Errors());
    }
}
