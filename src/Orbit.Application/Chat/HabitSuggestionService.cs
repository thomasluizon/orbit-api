using System.Text.Json;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal sealed record PreparedHabitSuggestion(HabitSuggestionDraft Draft, JsonElement Arguments);

internal sealed class HabitSuggestionSetFailure(string error, int draftIndex)
    : Result<IReadOnlyList<PreparedHabitSuggestion>>(null, false, error, "HABIT_SUGGESTION_UNAVAILABLE")
{
    public int DraftIndex { get; } = draftIndex;
}

public sealed class HabitSuggestionService(IGenericRepository<Habit> habits, IGenericRepository<Goal> goals,
    IUserDateService dates, IHabitDraftGenerator generator)
{
    public async Task<Result<HabitSuggestionsCard>> GenerateAsync(Guid userId, string instruction, string language,
        int count, HabitSuggestionDraft? original, CancellationToken ct)
    {
        var today = await dates.GetUserTodayAsync(userId, ct);
        var active = await habits.FindAsync(habit => habit.UserId == userId && !habit.IsCompleted, ct);
        var activeGoals = await goals.FindAsync(goal => goal.UserId == userId && goal.Status == GoalStatus.Active, ct);
        var weekStart = await dates.GetUserWeekStartDayAsync(userId, ct);
        var request = new HabitSuggestionRequest(userId, instruction, language, count, today, active, activeGoals, original, WeekStartDay: weekStart);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var generated = await generator.GenerateAsync(request, ct);
            if (generated.IsFailure)
            {
                if (generated.ErrorCode != "HABIT_SUGGESTION_INVALID")
                    return generated.PropagateError<HabitSuggestionsCard>();
                request = request with { Correction = generated.Error };
                continue;
            }
            var result = Normalize(generated.Value, request);
            if (result.IsSuccess) return result;
            request = request with { Correction = result.Error };
        }
        return Result.Failure<HabitSuggestionsCard>(Unavailable(language), "HABIT_SUGGESTION_UNAVAILABLE");
    }

    internal async Task<Result<IReadOnlyList<PreparedHabitSuggestion>>> PrepareAllForPreviewAsync(Guid userId, IReadOnlyList<HabitSuggestionDraft> drafts,
        string language, CancellationToken ct)
    {
        var today = await dates.GetUserTodayAsync(userId, ct);
        var active = await habits.FindAsync(habit => habit.UserId == userId && !habit.IsCompleted, ct);
        var weekStart = await dates.GetUserWeekStartDayAsync(userId, ct);
        var result = Normalize(drafts, new(userId, string.Empty, language, drafts.Count, today, active, [], WeekStartDay: weekStart),
            out var draftIndex);
        if (result.IsFailure) return new HabitSuggestionSetFailure(Unavailable(language), draftIndex);
        var prepared = new List<PreparedHabitSuggestion>();
        for (var index = 0; index < result.Value.Drafts.Count; index++)
        {
            var candidate = result.Value.Drafts[index];
            var target = candidate.ExistingHabitId is { } existing ? active.Single(habit => habit.Id == existing) : null;
            var arguments = PreviewHabitSuggestionCommandHandler.Arguments(candidate, target);
            if (target is not null)
            {
                var update = ResolveUpdate(target, candidate, today, weekStart);
                if (update.IsFailure) return new HabitSuggestionSetFailure(Unavailable(language), index);
                var validation = await HabitUpdateValidation.ValidateAsync(userId, target, update.Value, habits, ct);
                if (validation.IsFailure) return new HabitSuggestionSetFailure(Unavailable(language), index);
            }
            prepared.Add(new(candidate, arguments));
        }
        return Result.Success<IReadOnlyList<PreparedHabitSuggestion>>(prepared);
    }

    internal static Result<HabitSuggestionsCard> Normalize(IReadOnlyList<HabitSuggestionDraft>? drafts, HabitSuggestionRequest request) =>
        Normalize(drafts, request, out _);

    private static Result<HabitSuggestionsCard> Normalize(IReadOnlyList<HabitSuggestionDraft>? drafts, HabitSuggestionRequest request,
        out int draftIndex)
    {
        draftIndex = 0;
        if (drafts is null || drafts.Count != request.Count || !HabitSuggestionsCard.IsValidDraftCount(drafts.Count))
            return Invalid("Return the requested number of drafts, between one and five.");
        var output = new List<HabitSuggestionDraft>();
        var scheduled = new List<Habit>();
        for (; draftIndex < drafts.Count; draftIndex++)
        {
            var draft = drafts[draftIndex];
            if (draft is null) return Invalid("A complete draft is required.");
            var candidate = request.Original is { } original
                ? draft with { Id = original.Id, ExistingHabitId = original.ExistingHabitId } : draft;
            if (candidate.ExistingHabitId is { } existing)
            {
                var target = request.Habits.SingleOrDefault(habit => habit.Id == existing);
                if (target is null) return Invalid("The existing habit must be active and owned by the person.");
                candidate = candidate with { Title = target.Title };
            }
            else
            {
                var matches = request.Habits.Where(habit => HabitNameMatcher.Normalize(habit.Title) == HabitNameMatcher.Normalize(candidate.Title ?? string.Empty)).ToArray();
                if (matches.Length > 1) return Invalid("Choose an unambiguous existing habit.");
                if (matches is [var match])
                    candidate = candidate with { ExistingHabitId = match.Id, Title = match.Title };
            }
            if (output.Any(other => SameSuggestion(other, candidate)))
                return Invalid("Each draft must propose a distinct habit.");
            var entity = candidate.ToHabit(request.UserId);
            if (entity.IsFailure) return Invalid(entity.Error);
            if (candidate.ExistingHabitId is { } targetId)
            {
                var target = request.Habits.Single(habit => habit.Id == targetId);
                var update = ResolveUpdate(target, candidate, request.Today, request.WeekStartDay);
                if (update.IsFailure) return Invalid(update.Error);
                var relations = HabitUpdateValidation.ValidateGeneral(target, false, request.Habits);
                if (relations.IsFailure) return Invalid(relations.Error);
                entity = target.PreviewUpdate(update.Value);
                if (entity.IsFailure) return Invalid(entity.Error);
            }
            if (candidate.DueDate < request.Today || candidate.DueDate > request.Today.AddDays(366))
                return Invalid("Choose a local start date within the coming year.");
            if (HasScheduleConflict(entity.Value, candidate, request.Habits.Concat(scheduled), request.WeekStartDay))
                return Invalid("Choose a free time that does not overlap the person's habits or another draft.");
            output.Add(candidate);
            scheduled.Add(entity.Value);
        }
        return Result.Success(new HabitSuggestionsCard(request.Language, output));
    }

    private static Result<HabitUpdateParams> ResolveUpdate(Habit target, HabitSuggestionDraft draft, DateOnly today, int weekStart)
    {
        var (changes, error) = BulkHabitToolArguments.ParseUpdateFields(PreviewHabitSuggestionCommandHandler.Arguments(draft, target), allowHabitId: true);
        if (error is not null) return Result.Failure<HabitUpdateParams>(error);
        var update = HabitUpdateValidation.ResolveSingleUpdate(target, changes!, today);
        return update.IsSuccess ? Result.Success(update.Value with { WeekStartDay = weekStart }) : update;
    }

    private static Result<HabitSuggestionsCard> Invalid(string reason) => Result.Failure<HabitSuggestionsCard>(reason, "HABIT_SUGGESTION_INVALID");
    private static bool HasScheduleConflict(Habit entity, HabitSuggestionDraft draft, IEnumerable<Habit> habits, int weekStart)
    {
        var timed = habits.Where(habit => habit.Id != draft.ExistingHabitId && habit.DueTime.HasValue).ToArray();
        return HabitScheduleService.GetScheduledDates(entity, draft.DueDate, draft.DueDate.AddDays(366), weekStart)
            .Any(day => timed.Any(habit => HabitScheduleService.IsHabitDueOnDate(habit, day, weekStart)
                && draft.DueTime.ToTimeSpan() < (habit.DueEndTime?.ToTimeSpan() ?? habit.DueTime.GetValueOrDefault().ToTimeSpan().Add(TimeSpan.FromMinutes(1)))
                && draft.DueEndTime > habit.DueTime));
    }
    private static bool SameSuggestion(HabitSuggestionDraft other, HabitSuggestionDraft candidate) =>
        other.Id == candidate.Id || HabitNameMatcher.Normalize(other.Title) == HabitNameMatcher.Normalize(candidate.Title)
        || candidate.ExistingHabitId.HasValue && other.ExistingHabitId == candidate.ExistingHabitId;
    internal static string Unavailable(string language) => ErrorCopy.Resolve("HABIT_SUGGESTION_UNAVAILABLE", LocaleHelper.IsPortuguese(language));
}
