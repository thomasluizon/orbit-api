using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Queries;

public record RescheduleSuggestionResponse(RescheduleSuggestion Suggestion, bool FromCache);

public record GetRescheduleSuggestionQuery(
    Guid UserId,
    Guid HabitId,
    string Language) : IRequest<Result<RescheduleSuggestionResponse>>;

public class GetRescheduleSuggestionQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IPayGateService payGate,
    IRescheduleSuggestionService rescheduleService,
    IMemoryCache cache) : IRequestHandler<GetRescheduleSuggestionQuery, Result<RescheduleSuggestionResponse>>
{
    private const int LogHistoryWindowDays = 60;

    public async Task<Result<RescheduleSuggestionResponse>> Handle(
        GetRescheduleSuggestionQuery request,
        CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanUseSmartReschedule(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<RescheduleSuggestionResponse>();

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<RescheduleSuggestionResponse>(ErrorMessages.UserNotFound);

        var effectiveLanguage = ResolveLanguage(user.Language, request.Language);

        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, userTimeZone);
        var userToday = DateOnly.FromDateTime(userNow);

        var logWindowStart = userToday.AddDays(-LogHistoryWindowDays);
        var habits = await habitRepository.FindAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= logWindowStart && l.Date <= userToday)),
            cancellationToken);

        var habit = habits.FirstOrDefault();
        if (habit is null)
            return Result.Failure<RescheduleSuggestionResponse>(ErrorMessages.HabitNotFound);

        if (!HabitScheduleService.IsOverdueOnDate(habit, userToday))
            return Result.Failure<RescheduleSuggestionResponse>(ErrorMessages.HabitNotOverdue);

        var cacheKey = CacheKey(request.HabitId, habit.DueDate, effectiveLanguage);
        if (cache.TryGetValue(cacheKey, out RescheduleSuggestionResponse? cached) && cached is not null)
            return Result.Success(cached with { FromCache = true });

        var suggestionResult = await rescheduleService.GenerateAsync(
            habit, userToday, effectiveLanguage, cancellationToken);

        if (suggestionResult.IsFailure)
            return suggestionResult.PropagateError<RescheduleSuggestionResponse>();

        var response = new RescheduleSuggestionResponse(suggestionResult.Value, FromCache: false);

        cache.Set(cacheKey, response, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
        });

        return Result.Success(response);
    }

    private static string ResolveLanguage(string? userLanguage, string requestLanguage)
    {
        if (!string.IsNullOrWhiteSpace(userLanguage))
            return userLanguage;
        if (!string.IsNullOrWhiteSpace(requestLanguage))
            return requestLanguage;
        return "en";
    }

    private static string CacheKey(Guid habitId, DateOnly dueDate, string language) =>
        $"reschedule:{habitId}:{dueDate:yyyy-MM-dd}:{language.ToLowerInvariant()}";
}
