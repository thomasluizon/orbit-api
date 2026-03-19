using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record DailySummaryResponse(string Summary, bool FromCache);

public record GetDailySummaryQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    bool IncludeOverdue,
    string Language) : IRequest<Result<DailySummaryResponse>>;

public class GetDailySummaryQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    ISummaryService summaryService,
    IMemoryCache cache) : IRequestHandler<GetDailySummaryQuery, Result<DailySummaryResponse>>
{
    public async Task<Result<DailySummaryResponse>> Handle(
        GetDailySummaryQuery request,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<DailySummaryResponse>("User not found.");

        if (!user.HasProAccess)
            return Result.Failure<DailySummaryResponse>("Daily summaries are a Pro feature. Upgrade to unlock!");

        if (!user.AiSummaryEnabled)
            return Result.Failure<DailySummaryResponse>("AI summary is disabled.");

        var cacheKey = CacheKey(request.UserId, request.DateFrom, request.Language);

        if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        {
            return Result.Success(new DailySummaryResponse(cached, FromCache: true));
        }

        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsActive,
            cancellationToken);

        var summaryResult = await summaryService.GenerateSummaryAsync(
            habits,
            request.DateFrom,
            request.DateTo,
            request.IncludeOverdue,
            request.Language,
            cancellationToken);

        if (summaryResult.IsFailure)
            return Result.Failure<DailySummaryResponse>(summaryResult.Error);

        // Cache until end of day, minimum 5 minutes if expiry is in the past
        var endOfDay = new DateTimeOffset(request.DateFrom.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
        var expiry = endOfDay > DateTimeOffset.UtcNow ? endOfDay : DateTimeOffset.UtcNow.AddMinutes(5);

        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = expiry
        };

        cache.Set(cacheKey, summaryResult.Value, cacheOptions);

        return Result.Success(new DailySummaryResponse(summaryResult.Value, FromCache: false));
    }

    private static string CacheKey(Guid userId, DateOnly date, string language) =>
        $"summary:{userId}:{date:yyyy-MM-dd}:{language}";
}
