using Tikai.Core.Previews;
using Tikai.Orchestrator;
using Tikai.Orchestrator.Previews;

namespace Tikai.Core.Tests.Previews;

public class PreviewReconciliationTests
{
    private static readonly TimeSpan _maxLifetime = TimeSpan.FromHours(4);
    private static readonly Guid _a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid _b = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public void A_running_preview_that_is_used_keeps_running()
    {
        var actions = Plan([App(_a), Network(_a)], [Running(_a, idleFor: TimeSpan.FromMinutes(29))]);

        Assert.Empty(actions);
    }

    [Fact]
    public void A_preview_nobody_used_for_its_idle_minutes_is_stopped()
    {
        var stop = Assert.IsType<StopPreviewAction>(Assert.Single(Plan([App(_a)], [Running(_a, idleFor: TimeSpan.FromMinutes(30))])));

        Assert.Equal((_a, "Nobody used it for 30 minutes."), (stop.PreviewId, stop.Reason));
    }

    [Fact]
    public void A_preview_is_stopped_at_the_time_limit_even_when_it_is_used()
    {
        var stop = Assert.IsType<StopPreviewAction>(Assert.Single(Plan([App(_a)], [Running(_a, age: _maxLifetime)])));

        Assert.Equal("It ran for the limit of 4 hours.", stop.Reason);
    }

    [Theory]
    [InlineData(PreviewState.Preparing)]
    [InlineData(PreviewState.Starting)]
    [InlineData(PreviewState.Running)]
    public void A_preview_of_a_change_set_that_ended_is_stopped(PreviewState state)
    {
        var preview = Running(_a) with { State = state, ChangeSetOpen = false, HasLiveStartJob = state != PreviewState.Running };

        var stop = Assert.IsType<StopPreviewAction>(Assert.Single(Plan([App(_a)], [preview])));

        Assert.Equal("The change set was merged, pushed or closed.", stop.Reason);
    }

    [Theory]
    [InlineData(PreviewState.Preparing)]
    [InlineData(PreviewState.Starting)]
    public void A_start_nobody_works_on_any_more_fails(PreviewState state)
    {
        var preview = Running(_a) with { State = state, Apps = [], HasLiveStartJob = false };

        var fail = Assert.IsType<FailPreviewAction>(Assert.Single(Plan([], [preview])));

        Assert.Equal((_a, state, "The hub stopped while starting this preview. Start it again."), (fail.PreviewId, fail.State, fail.Reason));
    }

    [Fact]
    public void A_start_in_progress_is_left_alone_with_its_containers()
    {
        var preview = Running(_a) with { State = PreviewState.Preparing, Apps = [], HasLiveStartJob = true };

        Assert.Empty(Plan([Container(_a, PreviewRole.Setup, running: true), Network(_a)], [preview]));
    }

    [Fact]
    public void A_running_preview_whose_app_stopped_fails()
    {
        var fail = Assert.IsType<FailPreviewAction>(Assert.Single(Plan([App(_a, running: false)], [Running(_a)])));

        Assert.Equal("The app stopped.", fail.Reason);
    }

    [Fact]
    public void A_running_preview_whose_app_is_gone_fails()
    {
        var fail = Assert.IsType<FailPreviewAction>(Assert.Single(Plan([Network(_a)], [Running(_a)])));

        Assert.Equal("The app stopped.", fail.Reason);
    }

    [Fact]
    public void A_preview_with_several_apps_runs_while_every_app_runs()
    {
        var preview = Running(_a) with { Apps = [Upstream("web", "/"), Upstream("api", "/api")] };

        Assert.Empty(Plan([Container("web", _a), Container("api", _a), Network(_a)], [preview]));
    }

    [Fact]
    public void A_preview_fails_when_any_of_its_apps_stops()
    {
        var preview = Running(_a) with { Apps = [Upstream("web", "/"), Upstream("api", "/api")] };

        var fail = Assert.IsType<FailPreviewAction>(Assert.Single(Plan([Container("web", _a), Container("api", _a, running: false)], [preview])));

        Assert.Equal((PreviewState.Running, "The app api stopped."), (fail.State, fail.Reason));
    }

    [Fact]
    public void Containers_and_networks_of_ended_previews_are_torn_down_once()
    {
        var actions = Plan([App(_b), Container(_b, PreviewRole.Service, running: true), Network(_b)], []);

        Assert.Equal([new TeardownPreviewAction(_b)], actions);
    }

    [Fact]
    public void A_cleanup_container_belongs_to_its_teardown()
    {
        Assert.Empty(Plan([Container(_b, PreviewRole.Cleanup, running: true)], []));
    }

    [Fact]
    public void Ended_previews_whose_directory_is_still_there_are_torn_down()
    {
        Assert.Equal([new TeardownPreviewAction(_b)], Plan([], [], uncleaned: [_b]));
    }

    [Fact]
    public void A_teardown_is_not_planned_twice_for_one_preview()
    {
        Assert.Equal([new TeardownPreviewAction(_b)], Plan([Network(_b)], [], uncleaned: [_b]));
    }

    private static List<PreviewReconcileAction> Plan(IReadOnlyList<PreviewResource> resources, IReadOnlyList<ActivePreview> previews, IReadOnlyList<Guid>? uncleaned = null) =>
        [.. PreviewReconciliation.Plan(resources, previews, uncleaned ?? [], _maxLifetime)];

    private static ActivePreview Running(Guid id, TimeSpan? idleFor = null, TimeSpan? age = null) =>
        new(id, PreviewState.Running, ChangeSetOpen: true, Apps: [new PreviewAppUpstream("app", "/", "http://x", "app-" + id)], HasLiveStartJob: false,
            Age: age ?? TimeSpan.FromMinutes(40), IdleFor: idleFor ?? TimeSpan.FromMinutes(1), IdleMinutes: 30);

    private static PreviewResource App(Guid id, bool running = true) => new(false, "app-" + id, id, PreviewRole.App, running);

    private static PreviewResource Container(Guid id, PreviewRole role, bool running) => new(false, $"{role}-{id}", id, role, running);

    private static PreviewAppUpstream Upstream(string name, string path) => new(name, path, $"http://{name}", name + "-container");

    private static PreviewResource Container(string app, Guid id, bool running = true) => new(false, app + "-container", id, PreviewRole.App, running);

    private static PreviewResource Network(Guid id) => new(true, "net-" + id, id, null, false);
}
