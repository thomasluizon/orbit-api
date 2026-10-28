using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;
using Orbit.Domain.Models;

namespace Orbit.Domain.Entities;

public record HabitCreateParams(
    HabitSchedule Schedule,
    Guid UserId,
    string Title,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    DateOnly DueDate,
    string? Description = null,
    IReadOnlyList<System.DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    Guid? ParentHabitId = null,
    bool ReminderEnabled = false,
    IReadOnlyList<int>? ReminderTimes = null,
    bool SlipAlertEnabled = false,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    DateOnly? EndDate = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    int? Position = null,
    string? GoogleEventId = null,
    string? Emoji = null,
    int? IntervalWeeks = null,
    IReadOnlyList<RelativeReminderTime>? RelativeReminders = null,
    DateTime? CreatedAtUtc = null,
    DateOnly? StartDate = null,
    int WeekStartDay = 1,
    RepeatReminderOptions? RepeatReminder = null);

public record HabitUpdateParams(
    HabitSchedule Schedule,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<System.DayOfWeek>? Days,
    bool IsBadHabit,
    DateOnly? DueDate,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    bool? ReminderEnabled = null,
    IReadOnlyList<int>? ReminderTimes = null,
    bool? SlipAlertEnabled = null,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    bool ResetCompletion = false,
    DateOnly? EndDate = null,
    bool? ClearEndDate = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    string? Emoji = null,
    DateOnly? UserToday = null,
    int? IntervalWeeks = null,
    IReadOnlyList<RelativeReminderTime>? RelativeReminders = null,
    DateOnly? StartDate = null,
    int WeekStartDay = 1,
    RepeatReminderOptions? RepeatReminder = null,
    bool ClearRepeatReminder = false);

public class Habit : Entity, ITimestamped, ISoftDeletable
{
    private const int DaysInWeek = 7;
    private const int YearsInGregorianCycle = 400;
    private const int MonthsInGregorianCycle = YearsInGregorianCycle * 12;

    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public string? Emoji { get; private set; }
    public FrequencyUnit? FrequencyUnit { get; private set; }
    public int? FrequencyQuantity { get; private set; }
    public int? IntervalWeeks { get; private set; }
    public bool IsBadHabit { get; private set; }
    public bool IsCompleted { get; private set; }
    public HabitSchedule Schedule { get; private set; }
    /// <summary>
    /// The selected calendar start, changed only by an explicit recurring start-date edit.
    /// Moving the due-date cursor leaves this anchor unchanged.
    /// </summary>
    public DateOnly StartDate { get; private set; }
    public DateOnly NextDueDate { get; private set; }
    public DateOnly? ScheduledStartDate { get; private set; }
    /// <summary>
    /// For monthly/yearly habits, the original day-of-month from the first NextDueDate (1-31).
    /// Preserves the anchor across month-end clamping so a Jan 31 habit re-anchors to Mar 31
    /// rather than permanently drifting to day 28 after the Feb clamp.
    /// Null for daily/weekly habits and for legacy rows pre-migration (falls back to NextDueDate.Day).
    /// </summary>
    public int? OriginalDayOfMonth { get; private set; }
    public TimeOnly? DueTime { get; private set; }
    public TimeOnly? DueEndTime { get; private set; }
    public bool ReminderEnabled { get; private set; }
    public IReadOnlyList<int> ReminderTimes { get; private set; } = [15];
    public int? RepeatIntervalMinutes { get; private set; }
    public TimeOnly? RepeatActiveStart { get; private set; }
    public TimeOnly? RepeatActiveEnd { get; private set; }
    public DateTime? RepeatStartAtUtc { get; private set; }
    public RepeatReminderOptions? RepeatReminder => RepeatIntervalMinutes is { } minutes
        ? new(minutes, RepeatActiveStart, RepeatActiveEnd, RepeatStartAtUtc) : null;
    public bool SlipAlertEnabled { get; private set; }
    public IReadOnlyList<ChecklistItem> ChecklistItems { get; private set; } = [];
    public IReadOnlyList<ScheduledReminderTime> ScheduledReminders { get; private set; } = [];
    public IReadOnlyList<RelativeReminderTime> RelativeReminders { get; private set; } = [];
    public IReadOnlyList<ScheduledReminderTime> GetScheduledRemindersForLegacyClients() =>
        DueTime.HasValue
            ? RelativeReminders.Where(r => r.When.HasValue)
                .Select(r => new ScheduledReminderTime(r.When!.Value, r.Time!.Value))
                .ToList()
            : ScheduledReminders;
    public DateOnly? EndDate { get; private set; }
    public int? Position { get; private set; }
    public string? GoogleEventId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public long ReminderProbeVersion { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }
    public ICollection<System.DayOfWeek> Days { get; private set; } = [];

    public Guid? ParentHabitId { get; private set; }

    private readonly List<HabitLog> _logs = [];
    public IReadOnlyCollection<HabitLog> Logs => _logs.AsReadOnly();

    public void LoadScheduleLogsForRead(IEnumerable<HabitLog> logs)
    {
        var loaded = logs.ToList();
        if (loaded.Any(log => log.HabitId != Id))
            throw new ArgumentException("Schedule logs must belong to this habit.");

        _logs.Clear();
        _logs.AddRange(loaded);
    }

    private readonly List<Habit> _children = [];
    public IReadOnlyCollection<Habit> Children => _children.AsReadOnly();

    private readonly List<Tag> _tags = [];
    public IReadOnlyCollection<Tag> Tags => _tags.AsReadOnly();

    private readonly List<Goal> _goals = [];
    public IReadOnlyCollection<Goal> Goals => _goals.AsReadOnly();

    public void LoadScheduleRelationsForRead(IEnumerable<Tag> tags, IEnumerable<Goal> goals)
    {
        var loadedTags = tags.ToList();
        var loadedGoals = goals.ToList();
        if (loadedTags.Any(tag => tag.UserId != UserId)
            || loadedGoals.Any(goal => goal.UserId != UserId))
            throw new ArgumentException("Schedule relations must belong to this user.");

        _tags.Clear();
        _tags.AddRange(loadedTags);
        _goals.Clear();
        _goals.AddRange(loadedGoals);
    }

    private Habit() { }

    public static Habit FromScheduleSnapshot(HabitScheduleSnapshot snapshot, Guid userId) => new()
    {
        Id = snapshot.Id,
        UserId = userId,
        ParentHabitId = snapshot.ParentHabitId,
        FrequencyUnit = snapshot.FrequencyUnit,
        FrequencyQuantity = snapshot.FrequencyQuantity,
        IntervalWeeks = snapshot.IntervalWeeks,
        NextDueDate = snapshot.NextDueDate,
        StartDate = snapshot.StartDate,
        Schedule = snapshot.Schedule,
        ScheduledStartDate = snapshot.ScheduledStartDate,
        OriginalDayOfMonth = snapshot.OriginalDayOfMonth,
        EndDate = snapshot.EndDate,
        CreatedAtUtc = snapshot.CreatedAtUtc,
        DeletedAtUtc = snapshot.DeletedAtUtc,
        IsDeleted = snapshot.IsDeleted,
        IsBadHabit = snapshot.IsBadHabit,
        IsCompleted = snapshot.IsCompleted,
        Days = snapshot.Days.ToList(),
        Title = string.Empty
    };

    public void LoadScheduleCandidateFieldsForRead(
        string title, string? description, int? position, IEnumerable<Tag> tags)
    {
        Title = title;
        Description = description;
        Position = position;
        _tags.Clear();
        _tags.AddRange(tags);
    }

    public void LoadScheduleFieldsForRead(Habit candidate)
    {
        if (candidate.Id != Id || candidate.UserId != UserId)
            throw new ArgumentException("Schedule candidate must match the hydrated habit.");

        ParentHabitId = candidate.ParentHabitId;
        FrequencyUnit = candidate.FrequencyUnit;
        FrequencyQuantity = candidate.FrequencyQuantity;
        IntervalWeeks = candidate.IntervalWeeks;
        NextDueDate = candidate.NextDueDate;
        ScheduledStartDate = candidate.ScheduledStartDate;
        OriginalDayOfMonth = candidate.OriginalDayOfMonth;
        EndDate = candidate.EndDate;
        CreatedAtUtc = candidate.CreatedAtUtc;
        DeletedAtUtc = candidate.DeletedAtUtc;
        IsDeleted = candidate.IsDeleted;
        IsBadHabit = candidate.IsBadHabit;
        IsCompleted = candidate.IsCompleted;
        Days = candidate.Days.ToList();
    }

    public static Result<Habit> Create(HabitCreateParams p)
    {
        if (p.UserId == Guid.Empty)
            return Result.Failure<Habit>(DomainErrors.UserIdRequired);

        if (string.IsNullOrWhiteSpace(p.Title))
            return Result.Failure<Habit>(DomainErrors.TitleRequired);

        var polarityValidation = HabitSemantics.ValidatePolarity(p.Title, p.IsBadHabit);
        if (polarityValidation is not null)
            return Result.Failure<Habit>(polarityValidation);

        var emojiValidation = HabitInvariants.ValidateEmoji(p.Emoji);
        if (emojiValidation is not null)
            return Result.Failure<Habit>(emojiValidation);

        var scheduleValidation = HabitInvariants.ValidateScheduleOptions(
            p.Schedule, p.IsBadHabit, p.FrequencyUnit, p.FrequencyQuantity, p.Days, p.IntervalWeeks);
        if (scheduleValidation is not null)
            return Result.Failure<Habit>(scheduleValidation);

        var startValidation = HabitInvariants.ValidateStartDate(p.StartDate, p.Schedule == HabitSchedule.General, p.FrequencyUnit, p.EndDate);
        if (startValidation is not null)
            return Result.Failure<Habit>(startValidation);

        var dateValidation = HabitInvariants.ValidateDateOptions(
            p.DueTime, p.DueEndTime, p.EndDate, p.FrequencyUnit, p.Schedule == HabitSchedule.General, p.DueDate, p.CreatedAtUtc);
        if (dateValidation is not null)
            return Result.Failure<Habit>(dateValidation);

        var reminderValidation = HabitInvariants.ValidateScheduledReminders(p.ScheduledReminders);
        if (reminderValidation is not null)
            return Result.Failure<Habit>(reminderValidation);

        var reminderTimesValidation = HabitInvariants.ValidateReminderTimes(p.ReminderTimes);
        if (reminderTimesValidation is not null)
            return Result.Failure<Habit>(reminderTimesValidation);

        var relativeValidation = ValidateCreateRelativeReminders(p);
        if (relativeValidation is not null)
            return Result.Failure<Habit>(relativeValidation);

        if (p.RepeatReminder is { } repeat && (!repeat.IsValid()
            || !RepeatReminderOptions.SupportsSchedule(p.Schedule, p.FrequencyUnit, p.FrequencyQuantity, p.Days, p.IntervalWeeks)))
            return Result.Failure<Habit>(DomainErrors.InvalidRepeatReminder);

        return CreateWithSchedule(p);
    }

    private static Result<Habit> CreateWithSchedule(HabitCreateParams p)
    {
        var habit = CreateValidated(p);
        if (p.StartDate.HasValue)
        {
            habit.NextDueDate = p.StartDate.Value;
            var alignment = habit.AlignFirstOccurrence(p.WeekStartDay);
            if (alignment.IsFailure)
                return Result.Failure<Habit>(DomainErrors.RecurrenceScheduleUnsatisfiable);
        }
        return Result.Success(habit);
    }

    private static Habit CreateValidated(HabitCreateParams p)
    {
        var startDate = p.StartDate ?? p.DueDate;
        var schedule = p.Schedule;
        var habit = new Habit
        {
            UserId = p.UserId,
            Title = p.Title.Trim(),
            Description = p.Description?.Trim(),
            Emoji = HabitInvariants.NormalizeEmoji(p.Emoji),
            FrequencyUnit = p.FrequencyUnit,
            FrequencyQuantity = p.FrequencyQuantity,
            IntervalWeeks = p.IntervalWeeks,
            Days = schedule == HabitSchedule.Flexible ? [] : (p.Days?.ToList() ?? []),
            IsBadHabit = p.IsBadHabit,
            NextDueDate = p.DueDate,
            StartDate = startDate,
            Schedule = schedule,
            ScheduledStartDate = startDate,
            OriginalDayOfMonth = p.FrequencyUnit is Enums.FrequencyUnit.Month or Enums.FrequencyUnit.Year
                ? startDate.Day
                : null,
            DueTime = p.DueTime,
            DueEndTime = p.DueEndTime,
            ParentHabitId = p.ParentHabitId,
            ReminderEnabled = p.ReminderEnabled,
            ReminderTimes = p.ReminderTimes ?? (p.DueTime.HasValue && (p.ScheduledReminders is { Count: > 0 } || p.RelativeReminders is { Count: > 0 }) ? [] : [15]),
            SlipAlertEnabled = p.SlipAlertEnabled,
            ChecklistItems = p.ChecklistItems ?? [],
            ScheduledReminders = p.DueTime.HasValue ? [] : p.ScheduledReminders ?? [],
            RelativeReminders = p.DueTime.HasValue
                ? (p.RelativeReminders ?? []).Concat((p.ScheduledReminders ?? []).Select(RelativeReminderTime.FromScheduled)).Distinct().ToList()
                : p.RelativeReminders ?? [],
            EndDate = p.EndDate,
            Position = p.Position,
            GoogleEventId = p.GoogleEventId,
            CreatedAtUtc = ResolveCreatedAtUtc(p.CreatedAtUtc)
        };
        if (p.RepeatReminder is { } repeat)
            habit.SetRepeatReminder(repeat);
        return habit;
    }

    private static DateTime ResolveCreatedAtUtc(DateTime? createdAtUtc) => createdAtUtc ?? DateTime.UtcNow;

    public Result<HabitLog> Log(
        DateOnly date,
        string? note = null,
        bool advanceDueDate = true,
        int weekStartDay = 1,
        DateOnly? loggedOnLocalDate = null)
    {
        var validation = CheckLog(date, weekStartDay);
        if (validation.IsFailure)
            return new Result<HabitLog>(default, false, validation.Error, validation.ErrorCode, validation.ErrorArgs);

        var restoresDeletedDueDate = RestoresDeletedDueDate(date, advanceDueDate);
        var completionOrdinal = Schedule == HabitSchedule.Flexible || Schedule == HabitSchedule.General && RepeatIntervalMinutes.HasValue
            ? _logs.Where(l => l.Date == date && l.Value > 0)
                .Select(l => l.CompletionOrdinal)
                .DefaultIfEmpty(-1)
                .Max() + 1
            : 0;
        var log = HabitLog.Create(Id, date, 1, note, isSlip: IsBadHabit,
            completionOrdinal: completionOrdinal, loggedOnLocalDate: loggedOnLocalDate);
        _logs.Add(log);

        if (Schedule == HabitSchedule.OneTime)
        {
            IsCompleted = true;
        }
        else if (Schedule is HabitSchedule.Recurring or HabitSchedule.Flexible)
        {
            if (Schedule == HabitSchedule.Recurring && advanceDueDate)
                AdvanceDueDate(date, weekStartDay);
            else if (restoresDeletedDueDate)
            {
                var advanceResult = AdvancePastResolvedLogs(weekStartDay);
                if (advanceResult.IsFailure)
                    return Result.Failure<HabitLog>(DomainErrors.RecurrenceScheduleUnsatisfiable);
            }

            if (ChecklistItems.Count > 0)
                ChecklistItems = ChecklistItems.Select(i => i with { IsChecked = false }).ToList();
        }

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(log);
    }

    public Result CheckLog(DateOnly date, int weekStartDay)
    {
        if (IsCompleted && Schedule != HabitSchedule.General)
            return Result.Failure(DomainErrors.CannotLogCompletedHabit);
        if (!IsBadHabit && Schedule != HabitSchedule.Flexible && !(Schedule == HabitSchedule.General && RepeatIntervalMinutes.HasValue) && _logs.Exists(l => l.Date == date && !l.IsDeleted))
            return Result.Failure(DomainErrors.AlreadyLoggedForDate);
        if (Schedule == HabitSchedule.Flexible && !RepeatIntervalMinutes.HasValue && GetRemainingCompletions(date, _logs, weekStartDay) <= 0)
            return Result.Failure(DomainErrors.AllInstancesDone);
        return Result.Success();
    }

    private bool RestoresDeletedDueDate(DateOnly date, bool advanceDueDate) =>
        !advanceDueDate
        && Schedule != HabitSchedule.Flexible
        && !IsBadHabit
        && NextDueDate == date
        && _logs.Exists(l => l.Date == date && l.Value > 0 && l.IsDeleted);

    private Result AdvancePastResolvedLogs(int weekStartDay)
    {
        do
        {
            var result = AdvanceDueDate(NextDueDate, weekStartDay);
            if (result.IsFailure)
                return result;
        } while (_logs.Any(l => l.Date == NextDueDate && !l.IsDeleted));

        return Result.Success();
    }

    public int GetRemainingCompletions(DateOnly date, IReadOnlyCollection<HabitLog> logs, int weekStartDay)
    {
        var windowStart = FrequencyUnit switch
        {
            Enums.FrequencyUnit.Week => date.AddDays(-(((int)date.DayOfWeek - weekStartDay + 7) % 7)),
            Enums.FrequencyUnit.Month => new DateOnly(date.Year, date.Month, 1),
            Enums.FrequencyUnit.Year => new DateOnly(date.Year, 1, 1),
            _ => date
        };
        var windowEnd = FrequencyUnit switch
        {
            Enums.FrequencyUnit.Week => windowStart.AddDays(6),
            Enums.FrequencyUnit.Month => new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)),
            Enums.FrequencyUnit.Year => new DateOnly(date.Year, 12, 31),
            _ => date
        };
        var windowLogs = logs.Where(log => !log.IsDeleted && log.Date >= windowStart && log.Date <= windowEnd).ToList();
        var adjustedTarget = Math.Max(0, (FrequencyQuantity ?? 1) - windowLogs.Count(log => log.Value == 0));
        return Math.Max(0, adjustedTarget - windowLogs.Count(log => log.Value > 0));
    }

    public Result AdvanceDueDate(DateOnly today, int weekStartDay = 1)
    {
        var candidate = NextDueDate;
        var anchor = ScheduledStartDate ?? NextDueDate;

        do
        {
            var next = AdvanceDueDateByOneStep(candidate, anchor, weekStartDay);
            if (next.IsFailure)
                return Result.Failure(DomainErrors.RecurrenceScheduleUnsatisfiable);

            if (next.Value == candidate) break;
            candidate = next.Value;
        } while (candidate <= today);

        CaptureLegacyScheduledStart();
        NextDueDate = candidate;

        if (EndDate.HasValue && NextDueDate > EndDate.Value)
        {
            IsCompleted = true;
        }

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    /// Advances NextDueDate to the nearest scheduled date on or after today, without going past it.
    /// Used by background service to keep NextDueDate current for recurring habits.
    /// </summary>
    public Result CatchUpDueDate(DateOnly today, int weekStartDay = 1)
    {
        var shouldAdvance = NextDueDate < today && !IsCompleted;
        var candidate = NextDueDate;
        var anchor = ScheduledStartDate ?? NextDueDate;
        var shouldComplete = IsCompleted;

        while (candidate < today && !shouldComplete)
        {
            var next = AdvanceDueDateByOneStep(candidate, anchor, weekStartDay);
            if (next.IsFailure)
                return Result.Failure(DomainErrors.RecurrenceScheduleUnsatisfiable);

            if (next.Value == candidate) break;
            candidate = next.Value;

            if (EndDate.HasValue && candidate > EndDate.Value)
                shouldComplete = true;
        }

        if (shouldAdvance)
            CaptureLegacyScheduledStart();

        NextDueDate = candidate;
        IsCompleted = shouldComplete;

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    private void CaptureLegacyScheduledStart()
    {
        if (ScheduledStartDate is null)
            ScheduledStartDate = NextDueDate;
    }

    /// <summary>
    /// Advances NextDueDate by one frequency step, re-anchoring for monthly/yearly drift
    /// and snapping to the next matching day-of-week if Days are set.
    /// </summary>
    private Result<DateOnly> AdvanceDueDateByOneStep(
        DateOnly current,
        DateOnly anchor,
        int weekStartDay)
    {
        var advancement = AdvanceFrom(current);
        if (advancement.IsFailure)
            return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);

        var next = advancement.Value;
        var intervalWeeks = IntervalWeeks ?? 1;

        if (intervalWeeks <= 1)
            return Result.Success(next);

        var searchLimit = GetRecurrenceSearchLimit(intervalWeeks);
        for (var attempt = 0; attempt < searchLimit; attempt++)
        {
            var activeWeek = IsInActiveWeek(next, anchor, intervalWeeks, weekStartDay);
            if (activeWeek.IsFailure)
                return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);

            if (activeWeek.Value)
                return Result.Success(next);

            advancement = AdvanceFrom(next);
            if (advancement.IsFailure || advancement.Value <= next)
                return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);

            next = advancement.Value;
        }

        return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);
    }

    private int GetRecurrenceSearchLimit(int intervalWeeks)
    {
        var boundedInterval = Math.Min(intervalWeeks, DomainConstants.MaxIntervalWeeks);
        return FrequencyUnit switch
        {
            Enums.FrequencyUnit.Day => DaysInWeek * boundedInterval,
            Enums.FrequencyUnit.Week => boundedInterval,
            Enums.FrequencyUnit.Month => MonthsInGregorianCycle * boundedInterval,
            Enums.FrequencyUnit.Year => YearsInGregorianCycle * boundedInterval,
            _ => 1
        };
    }

    private Result<DateOnly> AdvanceFrom(DateOnly current)
    {
        try
        {
            var originalDay = OriginalDayOfMonth ?? current.Day;

            var next = (FrequencyUnit, FrequencyQuantity) switch
            {
                (Enums.FrequencyUnit.Day, var q) => current.AddDays(q!.Value),
                (Enums.FrequencyUnit.Week, var q) => current.AddDays(checked(DaysInWeek * q!.Value)),
                (Enums.FrequencyUnit.Month, var q) => current.AddMonths(q!.Value),
                (Enums.FrequencyUnit.Year, var q) => current.AddYears(q!.Value),
                _ => current
            };

            if (FrequencyUnit is Enums.FrequencyUnit.Month or Enums.FrequencyUnit.Year)
            {
                var daysInTargetMonth = DateTime.DaysInMonth(next.Year, next.Month);
                var correctedDay = Math.Min(originalDay, daysInTargetMonth);
                next = new DateOnly(next.Year, next.Month, correctedDay);
            }

            if (Days.Count > 0)
            {
                while (!Days.Contains(next.DayOfWeek))
                    next = next.AddDays(1);
            }

            return Result.Success(next);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);
        }
        catch (OverflowException)
        {
            return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);
        }
    }

    private static Result<bool> IsInActiveWeek(
        DateOnly target,
        DateOnly anchor,
        int intervalWeeks,
        int weekStartDay)
    {
        var targetWeekStart = GetWeekStart(target, weekStartDay);
        if (targetWeekStart.IsFailure)
            return Result.Failure<bool>(DomainErrors.RecurrenceScheduleUnsatisfiable);

        var anchorWeekStart = GetWeekStart(anchor, weekStartDay);
        if (anchorWeekStart.IsFailure)
            return Result.Failure<bool>(DomainErrors.RecurrenceScheduleUnsatisfiable);

        var weekDiff = (targetWeekStart.Value.DayNumber - anchorWeekStart.Value.DayNumber) / 7;
        return Result.Success(((weekDiff % intervalWeeks) + intervalWeeks) % intervalWeeks == 0);
    }

    private static Result<DateOnly> GetWeekStart(DateOnly date, int weekStartDay)
    {
        if (weekStartDay is < 0 or >= DaysInWeek)
            return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);

        var daysToStart = ((int)date.DayOfWeek - weekStartDay + 7) % 7;
        if (date.DayNumber < daysToStart)
            return Result.Failure<DateOnly>(DomainErrors.RecurrenceScheduleUnsatisfiable);

        return Result.Success(date.AddDays(-daysToStart));
    }

    /// <summary>
    /// Postpones a one-time task to the given date.
    /// </summary>
    public void PostponeTo(DateOnly date)
    {
        NextDueDate = date;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void InheritResolvedOccurrence(Habit parent)
    {
        if (ParentHabitId != parent.Id || UserId != parent.UserId)
            throw new ArgumentException("The resolved occurrence must belong to this habit's parent.", nameof(parent));

        NextDueDate = parent.NextDueDate;
        IsCompleted = parent.IsCompleted;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public Result<HabitLog> SkipFlexible(DateOnly date)
    {
        if (Schedule != HabitSchedule.Flexible)
            return Result.Failure<HabitLog>(DomainErrors.OnlyFlexibleHabitsSkippable);

        if (FrequencyUnit is null)
            return Result.Failure<HabitLog>(DomainErrors.CannotSkipOneTimeTask);

        var log = HabitLog.Create(Id, date, 0);
        _logs.Add(log);
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(log);
    }

    public Result RestoreSkip(HabitSkipUndo skip)
    {
        if (skip.HabitId != Id || skip.UserId != UserId)
            return Result.Failure(DomainErrors.SkipNotFound);
        if (skip.IsUndone || IsDeleted
            || UpdatedAtUtc.Ticks / 10 != skip.ExpectedUpdatedAtUtc.Ticks / 10)
            return Result.Failure(DomainErrors.SkipUndoConflict);

        if (skip.SkipLogId is { } logId)
        {
            var log = _logs.Find(l => l.Id == logId && l.Value == 0 && !l.IsDeleted);
            if (log is null)
                return Result.Failure(DomainErrors.SkipUndoConflict);
            log.SoftDelete();
        }

        NextDueDate = skip.PreviousDueDate;
        ScheduledStartDate = skip.PreviousScheduledStartDate;
        IsCompleted = skip.PreviousIsCompleted;
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public Result CorrectDose(Guid logId, DateTime takenAtUtc, DateTime nowAtUtc)
    {
        var log = _logs.Find(l => l.Id == logId && l.Value > 0 && !l.IsDeleted);
        if (log is null)
            return Result.Failure(DomainErrors.LogNotFoundForDate);
        var result = log.CorrectTakenAtUtc(takenAtUtc, nowAtUtc);
        if (result.IsSuccess)
            UpdatedAtUtc = nowAtUtc;
        return result;
    }

    public Result<HabitLog> Unlog(DateOnly date)
    {
        var log = _logs.Find(l => l.Date == date && l.Value > 0 && !l.IsDeleted);
        if (log is null)
            return Result.Failure<HabitLog>(DomainErrors.LogNotFoundForDate);

        log.SoftDelete();

        if (Schedule is HabitSchedule.OneTime or HabitSchedule.General)
        {
            IsCompleted = false;
        }
        else if (Schedule == HabitSchedule.Recurring)
        {
            NextDueDate = date;
            if (IsCompleted)
                IsCompleted = false;
        }

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(log);
    }

    public Result Update(HabitUpdateParams p, IReadOnlyCollection<Tag>? tags, int maxTags)
    {
        if (tags is not null)
        {
            if (tags.Count > maxTags)
                return Result.Failure(DomainErrors.MaxTagsPerHabit.Format(maxTags));
            if (tags.Any(tag => tag.UserId != UserId || tag.IsDeleted))
                return Result.Failure(DomainErrors.TagNotFound);
        }

        var result = Update(p);
        if (result.IsFailure || tags is null)
            return result;

        _tags.Clear();
        foreach (var tag in tags.DistinctBy(tag => tag.Id))
            AddTag(tag);
        return Result.Success();
    }

    public Result Update(HabitUpdateParams p)
    {
        var validation = ValidateUpdate(p);
        if (validation.IsFailure)
            return validation;

        var previousDueTime = DueTime;
        var candidate = (Habit)MemberwiseClone();
        var scheduleResult = candidate.ApplyRequiredUpdates(p);
        if (scheduleResult.IsFailure)
            return scheduleResult;
        ApplyRequiredUpdatesFrom(candidate);
        ApplyOptionalUpdates(p);
        if (p.StartDate.HasValue && Schedule is HabitSchedule.Recurring or HabitSchedule.Flexible)
            RecomputeCompletionForEndDate();

        if (!previousDueTime.HasValue && DueTime.HasValue && p.ReminderTimes is null && RelativeReminders.Count > 0)
            ReminderTimes = [];

        if (!DueTime.HasValue && previousDueTime.HasValue && RelativeReminders.Count > 0)
        {
            ScheduledReminders = ScheduledReminders.Concat(RelativeReminders.Select(r =>
                r.When.HasValue
                    ? new ScheduledReminderTime(r.When.Value, r.Time!.Value)
                    : OffsetToScheduled(previousDueTime.Value, r.MinutesBefore!.Value)))
                .Distinct().ToList();
            RelativeReminders = [];
        }

        if (p.ClearRepeatReminder)
        {
            RepeatIntervalMinutes = null;
            RepeatActiveStart = null;
            RepeatActiveEnd = null;
            RepeatStartAtUtc = null;
        }
        else if (p.RepeatReminder is { } repeat)
            SetRepeatReminder(repeat);
        else if (RepeatIntervalMinutes.HasValue)
        {
            ReminderTimes = [];
            ScheduledReminders = [];
            RelativeReminders = [];
        }
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    private void SetRepeatReminder(RepeatReminderOptions repeat)
    {
        RepeatIntervalMinutes = repeat.IntervalMinutes;
        RepeatActiveStart = repeat.ActiveStart;
        RepeatActiveEnd = repeat.ActiveEnd;
        RepeatStartAtUtc = repeat.StartAtUtc ?? RepeatStartAtUtc ?? DateTime.UtcNow;
        ReminderTimes = [];
        ScheduledReminders = [];
        RelativeReminders = [];
    }

    public Result<Habit> PreviewUpdate(HabitUpdateParams p)
    {
        var preview = (Habit)MemberwiseClone();
        var update = preview.Update(p);
        return update.IsSuccess
            ? Result.Success(preview)
            : Result.Failure<Habit>(update.Error);
    }

    private static AppError? ValidateCreateRelativeReminders(HabitCreateParams p)
    {
        var validation = HabitInvariants.ValidateRelativeReminders(p.RelativeReminders);
        if (validation is not null)
            return validation;

        if (p.DueTime.HasValue)
            return (p.RelativeReminders?.Count ?? 0) + (p.ScheduledReminders?.Count ?? 0) <= DomainConstants.MaxRelativeReminders
                ? null : DomainErrors.InvalidRelativeReminders;

        return p.RelativeReminders is { Count: > 0 } ? DomainErrors.InvalidRelativeReminders : null;
    }

    public Result ValidateUpdate(HabitUpdateParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Title))
            return Result.Failure(DomainErrors.TitleRequired);

        if ((p.Title.Trim() != Title || p.IsBadHabit != IsBadHabit)
            && HabitSemantics.ValidatePolarity(p.Title, p.IsBadHabit) is { } polarityValidation)
            return Result.Failure(polarityValidation);

        var validationError = ValidateUpdateParams(p);
        if (validationError is not null)
            return Result.Failure(validationError);

        return Result.Success();
    }

    private AppError? ValidateUpdatedRepeat(HabitUpdateParams p)
    {
        var effective = p.ClearRepeatReminder ? null : p.RepeatReminder ?? RepeatReminder;
        return effective is { } repeat && (!repeat.IsValid()
            || !RepeatReminderOptions.SupportsSchedule(p.Schedule, p.FrequencyUnit, p.FrequencyQuantity, p.Days, p.IntervalWeeks))
            ? DomainErrors.InvalidRepeatReminder : null;
    }

    private AppError? ValidateUpdateParams(HabitUpdateParams p)
    {
        var repeatValidation = ValidateUpdatedRepeat(p);
        if (repeatValidation is not null)
            return repeatValidation;
        var schedule = p.Schedule;
        var effectiveIsGeneral = schedule == HabitSchedule.General;

        var scheduleValidation = HabitInvariants.ValidateScheduleOptions(
            schedule, p.IsBadHabit, p.FrequencyUnit, p.FrequencyQuantity, p.Days, p.IntervalWeeks);
        if (scheduleValidation is not null)
            return scheduleValidation;

        var dateValidation = HabitInvariants.ValidateDateOptions(
            p.DueTime, p.DueEndTime,
            effectiveIsGeneral && p.EndDate.HasValue ? p.EndDate
                : p.ClearEndDate == true || effectiveIsGeneral ? null : (p.EndDate ?? EndDate),
            p.FrequencyUnit, effectiveIsGeneral, p.StartDate ?? p.DueDate ?? NextDueDate);
        if (dateValidation is not null)
            return dateValidation;

        var startValidation = ValidateUpdatedStart(p, effectiveIsGeneral);
        if (startValidation is not null)
            return startValidation;

        var emojiValidation = HabitInvariants.ValidateEmoji(p.Emoji);
        if (emojiValidation is not null)
            return emojiValidation;

        var scheduledReminderValidation = HabitInvariants.ValidateScheduledReminders(p.ScheduledReminders);
        if (scheduledReminderValidation is not null)
            return scheduledReminderValidation;

        var relativeValidation = HabitInvariants.ValidateRelativeReminders(p.RelativeReminders);
        if (relativeValidation is not null)
            return relativeValidation;

        if (!p.DueTime.HasValue && !DueTime.HasValue && p.RelativeReminders is { Count: > 0 })
            return DomainErrors.InvalidRelativeReminders;

        var relativeCount = p.RelativeReminders is not null
            ? p.RelativeReminders.Count
            : RelativeReminders.Count(r => !r.When.HasValue)
                + (p.ScheduledReminders?.Count ?? RelativeReminders.Count(r => r.When.HasValue) + ScheduledReminders.Count);
        if (p.DueTime.HasValue && relativeCount > DomainConstants.MaxRelativeReminders)
            return DomainErrors.InvalidRelativeReminders;

        return HabitInvariants.ValidateReminderTimes(p.ReminderTimes);
    }

    private AppError? ValidateUpdatedStart(HabitUpdateParams p, bool effectiveIsGeneral)
    {
        var startValidation = HabitInvariants.ValidateStartDate(p.StartDate, effectiveIsGeneral, p.FrequencyUnit,
            p.ClearEndDate == true ? null : p.EndDate ?? EndDate);
        if (RequiresScheduleReanchor(p) && !p.UserToday.HasValue)
            return DomainErrors.InvalidStartDate;
        return startValidation;
    }

    public bool RequiresScheduleReanchor(HabitUpdateParams p) =>
        p.StartDate.HasValue && (p.StartDate.Value != StartDate || RecurrenceChanged(p));

    private bool RecurrenceChanged(HabitUpdateParams p) =>
        FrequencyUnit != p.FrequencyUnit
        || FrequencyQuantity != p.FrequencyQuantity
        || IntervalWeeks != p.IntervalWeeks
        || (Schedule == HabitSchedule.Flexible) != (p.Schedule == HabitSchedule.Flexible)
        || !Days.ToHashSet().SetEquals(p.Schedule == HabitSchedule.Flexible ? [] : p.Days ?? []);

    private Result ApplyRequiredUpdates(HabitUpdateParams p)
    {
        var schedule = p.Schedule;
        var enteringGeneral = schedule == HabitSchedule.General && Schedule != schedule;
        var effectiveIsFlexible = schedule == HabitSchedule.Flexible;
        var effectiveDays = effectiveIsFlexible ? [] : (p.Days?.ToList() ?? []);
        var effectiveDueDate = p.StartDate.HasValue ? NextDueDate : p.DueDate ?? NextDueDate;
        var reanchorSchedule = RequiresScheduleReanchor(p);
        var dueChanged = effectiveDueDate != NextDueDate;
        var recurrencePhaseChanged = RecurrenceChanged(p);

        Title = p.Title.Trim();
        Description = p.Description?.Trim();
        Emoji = HabitInvariants.NormalizeEmoji(p.Emoji);
        Schedule = schedule;
        if (enteringGeneral)
            IsCompleted = false;
        FrequencyUnit = p.FrequencyUnit;
        FrequencyQuantity = p.FrequencyQuantity;
        IntervalWeeks = p.IntervalWeeks;
        Days = effectiveDays;
        IsBadHabit = p.IsBadHabit;
        DueTime = p.DueTime;
        DueEndTime = p.DueEndTime;

        if (reanchorSchedule)
        {
            var reanchor = Reanchor(p);
            if (reanchor.IsFailure)
                return reanchor;
        }
        else if (effectiveDueDate != NextDueDate || recurrencePhaseChanged)
        {
            NextDueDate = effectiveDueDate;
            ScheduledStartDate = effectiveDueDate;
        }

        if (FrequencyUnit is Enums.FrequencyUnit.Month or Enums.FrequencyUnit.Year)
            OriginalDayOfMonth = reanchorSchedule ? StartDate.Day
                : recurrencePhaseChanged || dueChanged ? effectiveDueDate.Day
                : OriginalDayOfMonth ?? NextDueDate.Day;
        else
            OriginalDayOfMonth = null;
        return Result.Success();
    }

    private Result Reanchor(HabitUpdateParams p)
    {
        StartDate = p.StartDate!.Value;
        NextDueDate = StartDate;
        ScheduledStartDate = StartDate;
        OriginalDayOfMonth = StartDate.Day;
        IsCompleted = false;
        EndDate = p.ClearEndDate == true ? null : p.EndDate ?? EndDate;
        var alignment = AlignFirstOccurrence(p.WeekStartDay);
        if (alignment.IsFailure)
            return alignment;
        if (Schedule != HabitSchedule.Flexible)
        {
            var catchUp = CatchUpDueDate(p.UserToday!.Value, p.WeekStartDay);
            if (catchUp.IsFailure)
                return catchUp;
            while (_logs.Any(log => !log.IsDeleted && log.Date == NextDueDate))
            {
                var advance = AdvanceDueDate(NextDueDate, p.WeekStartDay);
                if (advance.IsFailure)
                    return advance;
            }
        }
        else if (NextDueDate < p.UserToday!.Value)
            NextDueDate = p.UserToday.Value;
        return Result.Success();
    }

    private Result AlignFirstOccurrence(int weekStartDay)
    {
        if (Days.Count == 0)
            return Result.Success();
        for (var attempt = 0; attempt < DaysInWeek * (IntervalWeeks ?? 1); attempt++)
        {
            var active = IsInActiveWeek(NextDueDate, StartDate, IntervalWeeks ?? 1, weekStartDay);
            if (active.IsFailure)
                return Result.Failure(active.Error);
            if (Days.Contains(NextDueDate.DayOfWeek) && active.Value)
                return Result.Success();
            if (NextDueDate == DateOnly.MaxValue)
                return Result.Failure(DomainErrors.RecurrenceScheduleUnsatisfiable);
            NextDueDate = NextDueDate.AddDays(1);
        }
        return Result.Failure(DomainErrors.RecurrenceScheduleUnsatisfiable);
    }

    private void ApplyRequiredUpdatesFrom(Habit candidate)
    {
        Title = candidate.Title;
        Description = candidate.Description;
        Emoji = candidate.Emoji;
        Schedule = candidate.Schedule;
        FrequencyUnit = candidate.FrequencyUnit;
        FrequencyQuantity = candidate.FrequencyQuantity;
        IntervalWeeks = candidate.IntervalWeeks;
        Days = candidate.Days;
        IsBadHabit = candidate.IsBadHabit;
        DueTime = candidate.DueTime;
        DueEndTime = candidate.DueEndTime;
        StartDate = candidate.StartDate;
        NextDueDate = candidate.NextDueDate;
        ScheduledStartDate = candidate.ScheduledStartDate;
        OriginalDayOfMonth = candidate.OriginalDayOfMonth;
        IsCompleted = candidate.IsCompleted;
    }

    private void ApplyOptionalUpdates(HabitUpdateParams p)
    {
        if (p.ResetCompletion)
            IsCompleted = false;
        if (p.ReminderEnabled.HasValue)
            ReminderEnabled = p.ReminderEnabled.Value;
        if (p.ReminderTimes is not null)
            ReminderTimes = p.ReminderTimes;
        if (p.SlipAlertEnabled.HasValue)
            SlipAlertEnabled = p.SlipAlertEnabled.Value;
        if (p.ChecklistItems is not null)
            ChecklistItems = p.ChecklistItems;
        if (p.ScheduledReminders is not null)
            ScheduledReminders = p.ScheduledReminders;
        if (p.RelativeReminders is not null)
            RelativeReminders = p.RelativeReminders;

        if (DueTime.HasValue)
        {
            if (p.RelativeReminders is null && p.ScheduledReminders is not null)
                RelativeReminders = RelativeReminders.Where(r => !r.When.HasValue)
                    .Concat(p.ScheduledReminders.Select(RelativeReminderTime.FromScheduled)).Distinct().ToList();
            else if (p.RelativeReminders is null && ScheduledReminders.Count > 0)
                RelativeReminders = RelativeReminders.Concat(ScheduledReminders.Select(RelativeReminderTime.FromScheduled))
                    .Distinct().ToList();
            ScheduledReminders = [];
        }

        if (p.ClearEndDate == true || Schedule == HabitSchedule.General)
            EndDate = null;
        else if (p.EndDate.HasValue)
            EndDate = p.EndDate.Value;

        if ((p.ClearEndDate == true || p.EndDate.HasValue) && Schedule is HabitSchedule.Recurring or HabitSchedule.Flexible)
            RecomputeCompletionForEndDate();
    }

    private static ScheduledReminderTime OffsetToScheduled(TimeOnly dueTime, int minutesBefore)
    {
        var minutes = (int)dueTime.ToTimeSpan().TotalMinutes - minutesBefore;
        var when = minutes >= 0 ? ScheduledReminderWhen.SameDay : ScheduledReminderWhen.DayBefore;
        var localMinutes = ((minutes % 1440) + 1440) % 1440;
        return new ScheduledReminderTime(when, TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(localMinutes)));
    }

    private void RecomputeCompletionForEndDate()
    {
        if (EndDate.HasValue && NextDueDate > EndDate.Value)
            IsCompleted = true;
        else if (IsCompleted)
            IsCompleted = false;
    }

    public void UpdateChecklist(IReadOnlyList<ChecklistItem> items)
    {
        ChecklistItems = items;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public Result CanAppendChecklistItem(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Result.Failure(DomainErrors.ChecklistItemRequired);
        if (text.Length > DomainConstants.MaxChecklistItemTextLength) return Result.Failure(DomainErrors.ChecklistItemTooLong);
        return ChecklistItems.Count >= DomainConstants.MaxChecklistItems
            ? Result.Failure(DomainErrors.ChecklistFull) : Result.Success();
    }

    public Result AppendChecklistItem(string text)
    {
        var validation = CanAppendChecklistItem(text);
        if (validation.IsFailure) return validation;
        UpdateChecklist([.. ChecklistItems, new ChecklistItem(text, false)]);
        return Result.Success();
    }

    public void SetPosition(int? position)
    {
        Position = position;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Assigns or updates the Google Calendar master event ID used by auto-sync dedupe.
    /// Used both at creation time from the sync review flow and by the one-time
    /// reconciliation pass that backfills pre-existing manually-imported habits.
    /// </summary>
    public void SetGoogleEventId(string? googleEventId)
    {
        GoogleEventId = googleEventId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SetParentHabitId(Guid? parentHabitId)
    {
        if (parentHabitId == Id || parentHabitId == Guid.Empty)
            throw new ArgumentException("A parent must identify a different habit.", nameof(parentHabitId));

        ParentHabitId = parentHabitId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SoftDelete() => SoftDelete(DateTime.UtcNow);

    /// <summary>
    /// Soft-deletes the habit at a caller-supplied instant so a parent and the sub-habits cascaded
    /// with it share one <see cref="DeletedAtUtc"/>. Restore uses that shared instant to bring back
    /// exactly the subtree a single delete removed, without resurrecting a child deleted earlier in
    /// an unrelated action.
    /// </summary>
    public void SoftDelete(DateTime deletedAtUtc)
    {
        IsDeleted = true;
        DeletedAtUtc = deletedAtUtc;
        UpdatedAtUtc = deletedAtUtc;
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAtUtc = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void AddTag(Tag tag) { if (!_tags.Contains(tag)) _tags.Add(tag); }

    public void RemoveTag(Tag tag) => _tags.Remove(tag);

    public void AddGoal(Goal goal)
    {
        if (_goals.Contains(goal))
            return;

        _goals.Add(goal);
        goal.AddHabit(this);
    }

    public void RemoveGoal(Goal goal)
    {
        if (!_goals.Remove(goal))
            return;

        goal.RemoveHabit(this);
    }

    /// <summary>
    /// Removes every goal link through the bidirectional entity methods so each affected goal can
    /// restore manual progress or preserve an earned completion according to its own invariants.
    /// </summary>
    public void RemoveAllGoals()
    {
        foreach (var goal in _goals.ToList())
            RemoveGoal(goal);
    }
}
