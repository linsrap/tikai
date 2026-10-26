using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tikai.Core.Jobs;
using Tikai.Data;
using Tikai.Data.Jobs;

namespace Tikai.IntegrationTests;

public class JobWorkerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_worker_runs_the_handler_and_removes_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = PostgresFixture.UniqueName("q");
        using var host = await StartAsync(queue);
        var id = await EnqueueAsync(host.Services, queue, """{"fail":false}""");

        var handled = await host.Services.GetRequiredService<Recorder>().WaitForAsync(id, TimeSpan.FromSeconds(10));
        Assert.Equal(1, handled.Attempts);
        await WaitUntilAsync(async () => await RowAsync(id) is null, TimeSpan.FromSeconds(5));
        await host.StopAsync(ct);
    }

    [Fact]
    public async Task A_throwing_handler_fails_the_job_and_keeps_the_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = PostgresFixture.UniqueName("q");
        using var host = await StartAsync(queue);
        var id = await EnqueueAsync(host.Services, queue, """{"fail":true}""");

        await host.Services.GetRequiredService<Recorder>().WaitForAsync(id, TimeSpan.FromSeconds(10));
        await WaitUntilAsync(async () => (await RowAsync(id))?.LastError is not null, TimeSpan.FromSeconds(5));

        var row = await RowAsync(id);
        Assert.NotNull(row);
        Assert.Contains("handler failed on purpose", row.LastError);
        Assert.Null(row.LockedBy);
        Assert.True(row.RunAt > row.CreatedAt, "the retry should be scheduled after a backoff");
        await host.StopAsync(ct);
    }

    private async Task<IHost> StartAsync(string queue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTikaiData(postgres.ConnectionString);
        builder.Services.AddSingleton<Recorder>();
        builder.Services.AddJobHandler<RecordingHandler>(queue);
        builder.Services.Configure<JobWorkerOptions>(o => o.PollInterval = TimeSpan.FromMilliseconds(100));
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<Guid> EnqueueAsync(IServiceProvider services, string queue, string payload)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(queue, payload);
    }

    private async Task<JobRecord?> RowAsync(Guid id)
    {
        await using var db = postgres.CreateContext();
        return await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met within {timeout}.");
            }
            await Task.Delay(100);
        }
    }

    internal sealed class Recorder
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<JobLease>> _seen = new();

        public void Record(JobLease job) => Get(job.Id).TrySetResult(job);

        public Task<JobLease> WaitForAsync(Guid id, TimeSpan timeout) => Get(id).Task.WaitAsync(timeout);

        private TaskCompletionSource<JobLease> Get(Guid id) => _seen.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    internal sealed class RecordingHandler(Recorder recorder) : IJobHandler
    {
        public string Queue => "set by AddJobHandler";

        public Task HandleAsync(JobLease job, CancellationToken cancellationToken)
        {
            recorder.Record(job);
            return job.Payload.Contains("true", StringComparison.Ordinal)
                ? throw new InvalidOperationException("handler failed on purpose")
                : Task.CompletedTask;
        }
    }
}
