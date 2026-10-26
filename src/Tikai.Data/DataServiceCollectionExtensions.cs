using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Tikai.Core.Events;
using Tikai.Core.Jobs;
using Tikai.Data.Events;
using Tikai.Data.Jobs;

namespace Tikai.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Registers the DbContext, the job queue and worker, and the event bus.</summary>
    public static IServiceCollection AddTikaiData(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return services.AddTikaiData(_ => connectionString);
    }

    /// <summary>
    /// Same, but the connection string is resolved when the data source is first used,
    /// so tools that only build the host (OpenAPI generation) need no database settings.
    /// </summary>
    public static IServiceCollection AddTikaiData(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new NpgsqlDataSourceBuilder(connectionString(sp)).Build());
        services.AddSingleton<EntityTimestampsInterceptor>();
        services.AddDbContext<TikaiDbContext>((sp, options) => options.UseTikai(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<EntityTimestampsInterceptor>()));

        services.AddOptions<JobQueueOptions>();
        services.AddOptions<JobWorkerOptions>();
        services.AddScoped<IJobQueue, PostgresJobQueue>();
        services.AddHostedService<JobWorker>();

        services.AddScoped<IEventPublisher, PostgresEventPublisher>();
        services.AddSingleton<PostgresEventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<PostgresEventBus>());
        services.AddHostedService(sp => sp.GetRequiredService<PostgresEventBus>());

        return services;
    }

    /// <summary>Registers the handler for one queue and makes the job worker poll that queue.</summary>
    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services, string queue)
        where THandler : class, IJobHandler
    {
        services.AddKeyedScoped<IJobHandler, THandler>(queue);
        services.Configure<JobWorkerOptions>(o => o.Queues.Add(queue));
        return services;
    }

    internal static DbContextOptionsBuilder UseTikai(
        this DbContextOptionsBuilder options,
        NpgsqlDataSource dataSource,
        EntityTimestampsInterceptor? interceptor = null)
    {
        options.UseNpgsql(dataSource).UseSnakeCaseNamingConvention();
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }
        return options;
    }
}
