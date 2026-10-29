using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public class QueryHabitsTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IUserDateService userDateService,
    IHabitScheduleLogReader scheduleLogReader) : IAiTool
{
    public string Name => "query_habits";
    public bool IsReadOnly => true;

    public string Description =>
        "Read individual habits, schedules, metrics and checklists. For an unstructured task and habit preview, use this lookup for existing IDs and stored titles; no calendar, habit month, profile or analytics read is needed. Use for habit listings, including compound requests for habits and goals; "
        + "pair with query_goals for the goals. A daily summary does not replace this listing. Call before answering habit questions; indexes do not ground replies. "
        + "For a habit's completion or skip state on a day, use date with is_completed null to include done, remaining and skipped entries. "
        + "For a requested skip, call skip_habit or bulk_skip_habits after resolving the target, even when isSkippedOnDate is true; this read does not perform that action. "
        + "For choosing records or resolving a write target, set for_selection=true and orbit_read_complete=false, then call ask. "
        + "A selection lookup never finishes the answer or authorizes a write, even with one candidate. Multiple matches require the person's choice.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            for_selection = new { type = JsonSchemaTypes.Boolean, description = "True for a choice or pick list, or to resolve an unclear write target. False only for a read answer. Selection requires ask before any write." },
            relative_period = RelativeReadPeriod.DaySchema(),
            search = new { type = JsonSchemaTypes.String, description = "Match every word to a title word prefix, ignoring case, accents and order." },
            date = new { type = JsonSchemaTypes.String, description = "Schedule on YYYY-MM-DD or today, including done entries. Null is_completed returns the full schedule." },
            include_overdue = new { type = JsonSchemaTypes.Boolean, description = "Include overdue entries with date. Default: true for today, false otherwise." },
            is_general = new { type = JsonSchemaTypes.Boolean, description = "Filter timeless habits; null includes both." },
            is_completed = new { type = JsonSchemaTypes.Boolean, description = "With date: false remaining, true done, null all. Without date: false active (default), true completed." },
            is_bad_habit = new { type = JsonSchemaTypes.Boolean, description = "Filter bad habits; null includes both." },
            frequency = new { type = JsonSchemaTypes.String, @enum = new[] { "Day", "Week", "Month", "Year", "OneTime" } },
            tag = new { type = JsonSchemaTypes.String, description = "Filter by tag name (case-insensitive)" },
            include_sub_habits = new { type = JsonSchemaTypes.Boolean, description = "Include children. Default: true." },
            include_metrics = new { type = JsonSchemaTypes.Boolean, description = "Include streak/completion metrics for progress questions. Default: false." },
            offset = new { type = JsonSchemaTypes.Integer, description = "Typed row offset. Default: 0." },
            limit = new
            {
                type = JsonSchemaTypes.Integer,
                minimum = 1,
                maximum = 200,
                description = "Page size. Use 50 unless the user requests a different page size. Asking for all habits keeps the default page size; use the returned nextCursor for further pages."
            }
        },
        required = Array.Empty<string>()
    };

    private sealed record HabitFilters(
        bool? IsCompleted,
        bool? IsGeneral,
        bool? IsBadHabit,
        FrequencyUnit? Frequency,
        bool FrequencyOneTime,
        DateOnly? Date,
        bool IncludeOverdue,
        string? Search,
        string? Tag);

    private sealed record ScheduleContext(DateOnly Date, HabitTodaySnapshot Facts);
    private sealed record HabitQueryResult(IReadOnlyList<Habit> Habits, ScheduleContext? Schedule);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return new ToolResult(false, Error: ErrorMessages.UserNotFound.Message);

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var filters = ParseFilters(args, today);

        var includeMetrics = args.TryGetProperty("include_metrics", out var metricsEl) && metricsEl.ValueKind == JsonValueKind.True;
        var includeSubs = !args.TryGetProperty("include_sub_habits", out var subsEl) || subsEl.ValueKind != JsonValueKind.False;
        var limit = 50;
        if (args.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(limitEl.GetInt32(), 1, 200);

        var query = await QueryHabitsAsync(userId, filters, today, user.WeekStartDay, includeMetrics, ct);
        var allHabits = query.Habits;
        var parentIds = allHabits.Where(h => h.ParentHabitId.HasValue).Select(h => h.ParentHabitId!.Value).Distinct().ToArray();
        var parentTitles = allHabits.ToDictionary(h => h.Id, h => h.Title);
        var missingParentIds = parentIds.Where(id => !parentTitles.ContainsKey(id)).ToArray();
        if (missingParentIds.Length > 0)
            foreach (var parent in await habitRepository.FindAsync(h => h.UserId == userId && missingParentIds.Contains(h.Id), ct))
                parentTitles[parent.Id] = parent.Title;
        var returnedIds = allHabits.Select(habit => habit.Id).ToHashSet();

        var results = allHabits
            .Where(h => h.ParentHabitId is null
                || !returnedIds.Contains(h.ParentHabitId.Value))
            .OrderBy(h => h.Position)
            .Take(limit)
            .ToList();

        var output = results.Count == 0 && query.Schedule is null
            ? "No habits found matching the given filters."
            : BuildOutput(results, allHabits, today, user.WeekStartDay, includeMetrics, includeSubs, query.Schedule, parentTitles);
        var rows = new List<HabitListCardItem>();
        foreach (var root in allHabits.Where(h => h.ParentHabitId is null || !returnedIds.Contains(h.ParentHabitId.Value))
            .OrderBy(h => h.Position).ThenBy(h => h.Id))
            AppendReadRows(root, 0, rows, allHabits, query.Schedule, today, includeSubs, parentTitles);
        var page = rows.Skip(Math.Max(0, JsonArgumentParser.GetOptionalInt(args, "offset") ?? 0)).Take(limit).ToList();
        return new ToolResult(true, EntityName: output,
            Payload: new HabitListCard(query.Schedule is null ? "all" : query.Schedule.Date == today ? "today" : "date",
                page,
                query.Schedule?.Date, TotalCount: rows.Count,
                SourceRevision: ReadRevision.Hash(new
                {
                    Rows = rows,
                    Sources = allHabits.OrderBy(h => h.Id).Select(h => new { h.Id, h.UpdatedAtUtc }),
                    ScheduleRevision = query.Schedule?.Facts.SourceRevision
                }))
            {
                Selection = BuildSelection(args, filters.Search, rows.Count, page),
                SkippedIds = query.Schedule?.Facts.SkippedIds.Select(id => id.ToString())
                    .Intersect(page.Select(item => item.Id)).ToHashSet() ?? []
            });
    }

    private static HabitSelectionCandidates? BuildSelection(JsonElement args, string? search, int total,
        IReadOnlyList<HabitListCardItem> page)
    {
        var hasPurpose = args.TryGetProperty("for_selection", out var selection)
            && selection.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var requested = hasPurpose && selection.ValueKind == JsonValueKind.True;
        var ambiguous = !string.IsNullOrWhiteSpace(search) && total > 1;
        return total > 0 && (requested || ambiguous)
            ? new HabitSelectionCandidates(search, page, requested || !hasPurpose) : null;
    }

    private static void AppendReadRows(Habit habit, int depth, List<HabitListCardItem> rows,
        IReadOnlyList<Habit> habits, ScheduleContext? schedule, DateOnly today, bool includeSubs, IReadOnlyDictionary<Guid, string> parentTitles)
    {
        var status = schedule is not null
            ? schedule.Facts.SkippedIds.Contains(habit.Id) ? HabitListCardBuilder.StatusNone
                : schedule.Facts.DoneTodayIds.Contains(habit.Id) ? HabitListCardBuilder.StatusDone
                : schedule.Facts.OverdueIds.Contains(habit.Id) ? HabitListCardBuilder.StatusOverdue
                : HabitListCardBuilder.StatusToday
            : habit.Schedule == HabitSchedule.General ? HabitListCardBuilder.StatusGeneral
                : GetResponseCompletion(habit, today) ? HabitListCardBuilder.StatusDone
                : habit.NextDueDate < today ? HabitListCardBuilder.StatusOverdue
                : habit.NextDueDate == today ? HabitListCardBuilder.StatusToday : HabitListCardBuilder.StatusNone;
        rows.Add(new(habit.Id.ToString(), habit.Title, habit.Emoji, depth, habit.IsBadHabit, status, habit.StartDate, habit.ParentHabitId,
            habit.ParentHabitId is { } parentId ? parentTitles.GetValueOrDefault(parentId) : null,
            habit.Tags.Select(t => new HabitTagItem(t.Id, t.Name, t.Color)).ToList(),
            schedule is null ? null : schedule.Facts.SkippedIds.Contains(habit.Id)));
        if (includeSubs)
            foreach (var child in habits.Where(item => item.ParentHabitId == habit.Id).OrderBy(item => item.Position))
                AppendReadRows(child, depth + 1, rows, habits, schedule, today, true, parentTitles);
    }

    private static HabitFilters ParseFilters(JsonElement args, DateOnly today)
    {
        var isCompleted = ParseBoolFilter(args, "is_completed");
        var isGeneral = ParseBoolFilter(args, "is_general");
        var isBadHabit = ParseBoolFilter(args, "is_bad_habit");

        var (frequency, frequencyOneTime) = ParseFrequencyFilter(args);
        var (dateFilter, includeOverdue) = ParseDateFilter(args, today);

        return new HabitFilters(isCompleted, isGeneral, isBadHabit, frequency, frequencyOneTime,
            dateFilter, includeOverdue, ParseStringFilter(args, "search"), ParseStringFilter(args, "tag"));
    }

    private static bool? ParseBoolFilter(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind != JsonValueKind.Null
            ? el.ValueKind == JsonValueKind.True
            : null;

    private static string? ParseStringFilter(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static (FrequencyUnit? Frequency, bool IsOneTime) ParseFrequencyFilter(JsonElement args)
    {
        if (!args.TryGetProperty("frequency", out var freqEl) || freqEl.ValueKind != JsonValueKind.String)
            return (null, false);

        var freqStr = freqEl.GetString() ?? string.Empty;
        if (freqStr.Equals("OneTime", StringComparison.OrdinalIgnoreCase))
            return (null, true);

        return Enum.TryParse<FrequencyUnit>(freqStr, true, out var parsed) ? (parsed, false) : (null, false);
    }

    private static (DateOnly? Date, bool IncludeOverdue) ParseDateFilter(JsonElement args, DateOnly today)
    {
        if (!args.TryGetProperty("date", out var dateEl) || dateEl.ValueKind != JsonValueKind.String)
            return (null, false);

        var dateStr = dateEl.GetString() ?? string.Empty;
        DateOnly date;
        if (dateStr.Equals("today", StringComparison.OrdinalIgnoreCase))
            date = today;
        else if (DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            date = parsed;
        else
            date = today;

        var includeOverdue = dateStr.Equals("today", StringComparison.OrdinalIgnoreCase);
        if (args.TryGetProperty("include_overdue", out var overdueEl) && overdueEl.ValueKind != JsonValueKind.Null)
            includeOverdue = overdueEl.ValueKind == JsonValueKind.True;

        return (date, includeOverdue);
    }

    private async Task<HabitQueryResult> QueryHabitsAsync(
        Guid userId,
        HabitFilters f,
        DateOnly today,
        int weekStartDay,
        bool includeMetrics,
        CancellationToken ct)
    {
        var normalizedSearch = NormalizeSearchValue(f.Search);
        var normalizedTag = NormalizeSearchValue(f.Tag);

        var deferMetrics = includeMetrics && (normalizedSearch is not null || normalizedTag is not null);
        var habits = await LoadCandidatesAsync(userId, f, today, includeMetrics, deferMetrics, ct);

        habits = habits.Where(habit => normalizedSearch is null || HabitNameMatcher.Matches(habit.Title, normalizedSearch))
            .Where(habit => normalizedTag is null || habit.Tags.Any(tag => HabitNameMatcher.Matches(tag.Name, normalizedTag)))
            .ToList();

        if (deferMetrics && habits.Count > 0)
        {
            var ids = habits.Select(habit => habit.Id).ToArray();
            habits = await habitRepository.FindAsync(habit => habit.UserId == userId && ids.Contains(habit.Id),
                query => query.Include(habit => habit.Tags).Include(habit => habit.Goals).Include(habit => habit.Logs).AsSplitQuery(), ct);
        }

        if (f.Date is DateOnly date)
        {
            var schedule = await HabitTodaySnapshot.ReadAsync(habits, date, weekStartDay, scheduleLogReader, ct);
            var scheduled = habits.Where(h =>
                (schedule.TodayIds.Contains(h.Id) || f.IncludeOverdue && schedule.OverdueIds.Contains(h.Id)
                    || f.IsCompleted is null && schedule.SkippedIds.Contains(h.Id))
                && (f.IsCompleted != false || !schedule.SkippedIds.Contains(h.Id))
                && (f.IsCompleted is null || schedule.DoneTodayIds.Contains(h.Id) == f.IsCompleted.Value));
            return new HabitQueryResult(scheduled.ToList(), new ScheduleContext(date, schedule));
        }

        var expectedCompletion = f.IsCompleted ?? false;
        return new HabitQueryResult(habits
            .Where(h => GetResponseCompletion(h, today) == expectedCompletion)
            .ToList(), null);
    }

    private async Task<IReadOnlyList<Habit>> LoadCandidatesAsync(Guid userId, HabitFilters f,
        DateOnly today, bool includeMetrics, bool deferMetrics, CancellationToken ct)
    {
        return await habitRepository.FindAsync(
            h => h.UserId == userId
                && (f.IsGeneral == null || (h.Schedule == HabitSchedule.General) == f.IsGeneral.Value)
                && (f.IsBadHabit == null || h.IsBadHabit == f.IsBadHabit.Value)
                && (!f.FrequencyOneTime || h.Schedule == HabitSchedule.OneTime || h.Schedule == HabitSchedule.General)
                && (f.Frequency == null || h.FrequencyUnit == f.Frequency.Value)
                && (!f.Date.HasValue || h.Schedule != HabitSchedule.General),
            q => IncludeCandidates(q, today, includeMetrics, deferMetrics),
            ct);
    }

    private static IQueryable<Habit> IncludeCandidates(IQueryable<Habit> query, DateOnly today,
        bool includeMetrics, bool deferMetrics)
    {
        var withRelations = query.Include(h => h.Tags).Include(h => h.Goals).AsSplitQuery();
        if (deferMetrics)
            return withRelations;
        if (includeMetrics)
            return withRelations.Include(h => h.Logs);
        return withRelations.Include(h => h.Logs.Where(l => !l.IsDeleted && l.Date == today && l.Value > 0));
    }

    private static string? NormalizeSearchValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant();
    }

    private static string BuildOutput(
        List<Habit> results,
        IReadOnlyList<Habit> allHabits,
        DateOnly today,
        int weekStartDay,
        bool includeMetrics,
        bool includeSubs,
        ScheduleContext? schedule,
        IReadOnlyDictionary<Guid, string> parentTitles)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Found {results.Count} habit(s):");
        if (schedule is not null)
        {
            var done = results.Count(habit => schedule.Facts.DoneTodayIds.Contains(habit.Id));
            var skipped = results.Count(habit => schedule.Facts.SkippedIds.Contains(habit.Id));
            sb.AppendLine(CultureInfo.InvariantCulture, $"Returned schedule for {schedule.Date:yyyy-MM-dd}: {done} done, {results.Count - done - skipped} remaining, {skipped} skipped (top-level entries).");
        }
        if (results.Count == 0)
            sb.AppendLine("No habits found matching the given filters.");
        sb.AppendLine();

        var suffixes = SiblingTitleDisambiguator.ComputeSuffixes(results);
        foreach (var habit in results)
        {
            sb.AppendLine(BuildHabitLine(
                habit,
                today,
                weekStartDay,
                includeMetrics,
                suffixes.GetValueOrDefault(habit.Id, string.Empty),
                schedule, parentTitles));

            if (includeSubs)
                AppendChildren(sb, allHabits, habit.Id, new OutputContext(today, weekStartDay, includeMetrics, schedule, parentTitles), 1);
        }

        return sb.ToString();
    }

    private static string BuildHabitLine(
        Habit habit,
        DateOnly today,
        int weekStartDay,
        bool includeMetrics,
        string dupSuffix,
        ScheduleContext? schedule,
        IReadOnlyDictionary<Guid, string> parentTitles)
    {
        var freqLabel = FormatFrequencyLabel(habit);
        var labels = BuildLabels(habit, today, weekStartDay, includeMetrics, schedule);
        var labelStr = labels.Count > 0 ? $" [{string.Join(" | ", labels)}]" : "";
        var details = HabitFieldSummary.From(habit, habit.ParentHabitId is { } parentId ? parentTitles.GetValueOrDefault(parentId) : null).Format();
        var detailSuffix = details.Length == 0 ? string.Empty : $" | {details}";
        var emojiLabel = string.IsNullOrWhiteSpace(habit.Emoji) ? "No emoji" : $"Emoji: {habit.Emoji}";
        return string.Create(CultureInfo.InvariantCulture, $"- \"{habit.Title}\"{dupSuffix} | ID: {habit.Id} | {emojiLabel} | {freqLabel} | Due: {habit.NextDueDate:yyyy-MM-dd}{labelStr}{detailSuffix}");
    }

    private static List<string> BuildLabels(Habit habit, DateOnly today, int weekStartDay, bool includeMetrics, ScheduleContext? schedule)
    {
        var labels = new List<string>();
        var isCompleted = GetResponseCompletion(habit, today);
        if (habit.Schedule == HabitSchedule.General) labels.Add("GENERAL");
        if (schedule is not null)
        {
            if (schedule.Facts.SkippedIds.Contains(habit.Id)) labels.Add("SKIPPED ON DATE");
            if (schedule.Facts.TodayIds.Contains(habit.Id))
                labels.Add(schedule.Date == today ? "DUE TODAY" : "SCHEDULED ON DATE");
            if (schedule.Facts.OverdueIds.Contains(habit.Id)) labels.Add("OVERDUE");
            if (schedule.Facts.DoneTodayIds.Contains(habit.Id))
                labels.Add(schedule.Date == today ? "DONE TODAY" : "DONE ON DATE");
        }
        else
        {
            if (habit.Schedule != HabitSchedule.General && !isCompleted && habit.NextDueDate < today) labels.Add("OVERDUE");
            if (habit.Schedule != HabitSchedule.General && !isCompleted && habit.NextDueDate == today) labels.Add("DUE TODAY");
        }
        AddNotDueLabel(labels, habit, today);
        if (habit.IsBadHabit) labels.Add("BAD HABIT");
        if (isCompleted) labels.Add("COMPLETED");


        if (habit.Goals.Count > 0) labels.Add($"Goals: {string.Join(", ", habit.Goals.Select(g => $"{g.Title} ({g.Id})"))}");

        AddMetricLabels(labels, habit, today, weekStartDay, includeMetrics);

        if (habit.ChecklistItems.Count > 0)
            labels.Add($"Checklist: {habit.ChecklistItems.Count(i => i.IsChecked)}/{habit.ChecklistItems.Count}");

        if (schedule is null && includeMetrics && habit.Logs.Any(l => l.Date == today))
            labels.Add("DONE");

        return labels;
    }

    private static bool GetResponseCompletion(Habit habit, DateOnly today) =>
        habit.Schedule == HabitSchedule.General
            ? HabitScheduleService.HasCompletedLogInRange(habit, today, today)
            : habit.IsCompleted;

    private static void AddNotDueLabel(List<string> labels, Habit habit, DateOnly today)
    {
        if (habit.Schedule != HabitSchedule.General && !GetResponseCompletion(habit, today) && habit.NextDueDate > today)
            labels.Add(string.Create(CultureInfo.InvariantCulture, $"NOT DUE: {habit.NextDueDate:yyyy-MM-dd}"));
    }

    private static void AddMetricLabels(
        List<string> labels,
        Habit habit,
        DateOnly today,
        int weekStartDay,
        bool includeMetrics)
    {
        if (!includeMetrics) return;
        var metrics = HabitMetricsCalculator.Calculate(habit, today, weekStartDay);
        if (metrics.CurrentStreak > 0) labels.Add($"Streak: {metrics.CurrentStreak}d");
        if (metrics.WeeklyCompletionRate > 0) labels.Add(string.Create(CultureInfo.InvariantCulture, $"Week: {metrics.WeeklyCompletionRate:F0}%"));
        if (metrics.TotalCompletions > 0) labels.Add($"Total: {metrics.TotalCompletions}");
    }

    private static string FormatFrequencyLabel(Habit habit)
    {
        if (habit.Schedule is HabitSchedule.OneTime or HabitSchedule.General)
            return "One-time";

        var unitName = habit.FrequencyUnit.ToString()!.ToLower();
        return habit.FrequencyQuantity == 1
            ? $"Every {unitName}"
            : $"Every {habit.FrequencyQuantity} {unitName}s";
    }

    private sealed record OutputContext(DateOnly Today, int WeekStartDay, bool IncludeMetrics, ScheduleContext? Schedule, IReadOnlyDictionary<Guid, string> ParentTitles);

    private static void AppendChildren(StringBuilder sb, IReadOnlyList<Habit> allHabits, Guid parentId, OutputContext context, int depth)
    {
        var indent = new string(' ', depth * 2);
        var children = allHabits
            .Where(h => h.ParentHabitId == parentId)
            .OrderBy(h => h.Position)
            .ToList();
        var childSuffixes = SiblingTitleDisambiguator.ComputeSuffixes(children);

        foreach (var child in children)
        {
            sb.AppendLine(indent + BuildHabitLine(child, context.Today, context.WeekStartDay, context.IncludeMetrics,
                childSuffixes.GetValueOrDefault(child.Id, string.Empty), context.Schedule, context.ParentTitles));
            AppendChildren(sb, allHabits, child.Id, context, depth + 1);
        }
    }
}
