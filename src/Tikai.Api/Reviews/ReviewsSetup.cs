namespace Tikai.Api.Reviews;

/// <summary>Submissions (the git part of submit_for_review) and the review screen's API.</summary>
internal static class ReviewsSetup
{
    public static IServiceCollection AddTikaiReviews(this IServiceCollection services)
    {
        services.AddSingleton<SubmitLocks>();
        services.AddScoped<ReviewSubmissions>();
        return services;
    }
}
