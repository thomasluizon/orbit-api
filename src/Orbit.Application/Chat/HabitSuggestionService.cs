using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed class HabitSuggestionService(IGenericRepository<Habit> habits, IGenericRepository<Goal> goals,
    IUserDateService dates, IHabitDraftGenerator generator)
{
    public async Task<Result<HabitSuggestionsCard>> GenerateAsync(Guid userId, string instruction, string language,
        int count, HabitSuggestionDraft? original, CancellationToken ct)
    {
        var today = await dates.GetUserTodayAsync(userId, ct);
        var active = await habits.FindAsync(habit => habit.UserId == userId && !habit.IsCompleted, ct);
        var activeGoals = await goals.FindAsync(goal => goal.UserId == userId && goal.Status == GoalStatus.Active, ct);
        var request = new HabitSuggestionRequest(userId, instruction, language, count, today, active, activeGoals, original);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var generated = await generator.GenerateAsync(request, ct);
            if (generated.IsFailure) return generated.PropagateError<HabitSuggestionsCard>();
            var result = Normalize(generated.Value, request);
            if (result.IsSuccess) return result;
            request = request with { Correction = result.Error };
        }
        return Result.Failure<HabitSuggestionsCard>(Unavailable(language), "HABIT_SUGGESTION_INVALID");
    }

    public async Task<Result<HabitSuggestionDraft>> CheckForPreviewAsync(Guid userId, HabitSuggestionDraft draft,
        string language, CancellationToken ct)
    {
        var today = await dates.GetUserTodayAsync(userId, ct);
        var active = await habits.FindAsync(habit => habit.UserId == userId && !habit.IsCompleted, ct);
        var result = Normalize([draft], new(userId, string.Empty, language, 1, today, active, []));
        return result.IsSuccess ? Result.Success(result.Value.Drafts[0])
            : Result.Failure<HabitSuggestionDraft>(Unavailable(language), "HABIT_SUGGESTION_INVALID");
    }

    internal static Result<HabitSuggestionsCard> Normalize(IReadOnlyList<HabitSuggestionDraft>? drafts, HabitSuggestionRequest request)
    {
        if (drafts is null || drafts.Count != request.Count || drafts.Count is < 1 or > 5)
            return Invalid("Return the requested number of drafts, between one and five.");
        var output = new List<HabitSuggestionDraft>();
        var scheduled = new List<Habit>();
        foreach (var draft in drafts)
        {
            if (draft is null) return Invalid("A complete draft is required.");
            var candidate = request.Original is { } original
                ? draft with { Id = original.Id, ExistingHabitId = original.ExistingHabitId } : draft;
            var matches = request.Habits.Where(habit => HabitNameMatcher.Normalize(habit.Title) == HabitNameMatcher.Normalize(candidate.Title ?? string.Empty)).ToArray();
            if (matches.Length > 1) return Invalid("Choose an unambiguous existing habit.");
            if (matches is [var match])
            {
                if (candidate.ExistingHabitId is { } target && target != match.Id)
                    return Invalid("Keep the existing target rather than duplicating another habit.");
                candidate = candidate with { ExistingHabitId = match.Id };
            }
            if (candidate.ExistingHabitId is { } existing && !request.Habits.Any(habit => habit.Id == existing))
                return Invalid("The existing habit must be active and owned by the person.");
            if (output.Any(other => SameSuggestion(other, candidate)))
                return Invalid("Each draft must propose a distinct habit.");
            var entity = candidate.ToHabit(request.UserId);
            if (entity.IsFailure) return Invalid(entity.Error);
            if (candidate.DueDate < request.Today || candidate.DueDate > request.Today.AddDays(366))
                return Invalid("Choose a local start date within the coming year.");
            var windowEnd = candidate.DueDate.AddDays(366);
            foreach (var day in HabitScheduleService.GetScheduledDates(entity.Value, candidate.DueDate, windowEnd))
            {
                if (request.Habits.Concat(scheduled).Any(habit => habit.Id != candidate.ExistingHabitId
                    && habit.DueTime is { } start && HabitScheduleService.IsHabitDueOnDate(habit, day)
                    && candidate.DueTime < (habit.DueEndTime ?? start.AddMinutes(1)) && candidate.DueEndTime > start))
                    return Invalid("Choose a free time that does not overlap the person's habits or another draft.");
            }
            output.Add(candidate);
            scheduled.Add(entity.Value);
        }
        return Result.Success(new HabitSuggestionsCard(request.Language, output));
    }

    private static Result<HabitSuggestionsCard> Invalid(string reason) => Result.Failure<HabitSuggestionsCard>(reason, "HABIT_SUGGESTION_INVALID");
    private static bool SameSuggestion(HabitSuggestionDraft other, HabitSuggestionDraft candidate) =>
        other.Id == candidate.Id || HabitNameMatcher.Normalize(other.Title) == HabitNameMatcher.Normalize(candidate.Title)
        || candidate.ExistingHabitId.HasValue && other.ExistingHabitId == candidate.ExistingHabitId;
    internal static string Unavailable(string language) => LocaleHelper.IsPortuguese(language)
        ? "Não consegui preparar sugestões para sua agenda. Tente ajustar o pedido."
        : "I couldn't prepare suggestions for your schedule. Try adjusting your request.";
}
