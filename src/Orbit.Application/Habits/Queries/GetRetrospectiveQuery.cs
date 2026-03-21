using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record RetrospectiveResponse(string Retrospective, bool FromCache);

public record GetRetrospectiveQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    string Period,
    string Language) : IRequest<Result<RetrospectiveResponse>>;

public class GetRetrospectiveQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IPayGateService payGate,
    IRetrospectiveService retrospectiveService,
    IMemoryCache cache) : IRequestHandler<GetRetrospectiveQuery, Result<RetrospectiveResponse>>
{
    public async Task<Result<RetrospectiveResponse>> Handle(
        GetRetrospectiveQuery request,
        CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanUseRetrospective(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<RetrospectiveResponse>();

        var cacheKey = $"retro:{request.UserId}:{request.Period}:{request.DateFrom}:{request.Language}";

        if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
            return Result.Success(new RetrospectiveResponse(cached, FromCache: true));

        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= request.DateFrom && l.Date <= request.DateTo)),
            cancellationToken);

        var habitList = habits.ToList();

        if (habitList.Count == 0)
            return Result.Failure<RetrospectiveResponse>("No habits found for this period.");

        var result = await retrospectiveService.GenerateRetrospectiveAsync(
            habitList,
            request.DateFrom,
            request.DateTo,
            request.Period,
            request.Language,
            cancellationToken);

        if (result.IsFailure)
            return Result.Failure<RetrospectiveResponse>(result.Error);

        cache.Set(cacheKey, result.Value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
        });

        return Result.Success(new RetrospectiveResponse(result.Value, FromCache: false));
    }
}
