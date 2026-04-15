using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

#pragma warning disable S6964 // Domain entity with private setters - not a model-bound DTO

namespace Orbit.Domain.Entities;

public record HabitCreateParams(
    Guid UserId,
    string Title,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    string? Description = null,
    IReadOnlyList<System.DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    DateOnly? DueDate = null,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    Guid? ParentHabitId = null,
    bool ReminderEnabled = false,
    IReadOnlyList<int>? ReminderTimes = null,
    bool SlipAlertEnabled = false,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    bool IsGeneral = false,
    bool IsFlexible = false,
    DateOnly? EndDate = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    int? Position = null,
    string? GoogleEventId = null);

public record HabitUpdateParams(
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
    bool? IsGeneral = null,
    bool? IsFlexible = null,
    DateOnly? EndDate = null,
    bool? ClearEndDate = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null);

public class Habit : Entity, ITimestamped, ISoftDeletable
{
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public FrequencyUnit? FrequencyUnit { get; private set; }
    public int? FrequencyQuantity { get; private set; }
    public bool IsBadHabit { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateOnly DueDate { get; private set; }
    /// <summary>
    /// For monthly/yearly habits, the original day-of-month from the first DueDate (1-31).
    /// Preserves the anchor across month-end clamping so a Jan 31 habit re-anchors to Mar 31
    /// rather than permanently drifting to day 28 after the Feb clamp.
    /// Null for daily/weekly habits and for legacy rows pre-migration (falls back to DueDate.Day).
    /// </summary>
    public int? OriginalDayOfMonth { get; private set; }
    public TimeOnly? DueTime { get; private set; }
    public TimeOnly? DueEndTime { get; private set; }
    public bool ReminderEnabled { get; private set; }
    public IReadOnlyList<int> ReminderTimes { get; private set; } = [15];
    public bool IsGeneral { get; private set; }
    public bool IsFlexible { get; private set; }
    public bool SlipAlertEnabled { get; private set; }
    public IReadOnlyList<ChecklistItem> ChecklistItems { get; private set; } = [];
    public IReadOnlyList<ScheduledReminderTime> ScheduledReminders { get; private set; } = [];
    public DateOnly? EndDate { get; private set; }
    public int? Position { get; private set; }
    public string? GoogleEventId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }
    public ICollection<System.DayOfWeek> Days { get; private set; } = [];

    public Guid? ParentHabitId { get; private set; }

    private readonly List<HabitLog> _logs = [];
    public IReadOnlyCollection<HabitLog> Logs => _logs.AsReadOnly();

    private readonly List<Habit> _children = [];
    public IReadOnlyCollection<Habit> Children => _children.AsReadOnly();

    private readonly List<Tag> _tags = [];
    public IReadOnlyCollection<Tag> Tags => _tags.AsReadOnly();

    private readonly List<Goal> _goals = [];
    public IReadOnlyCollection<Goal> Goals => _goals.AsReadOnly();

    private Habit() { }

    public static Result<Habit> Create(HabitCreateParams p)
    {
        if (p.UserId == Guid.Empty)
            return Result.Failure<Habit>("User ID is required.");

        if (string.IsNullOrWhiteSpace(p.Title))
            return Result.Failure<Habit>("Title is required.");

        var scheduleValidation = ValidateScheduleOptions(
            p.IsGeneral, p.IsFlexible, p.IsBadHabit, p.FrequencyUnit, p.FrequencyQuantity, p.Days);
        if (scheduleValidation is not null)
            return Result.Failure<Habit>(scheduleValidation);

        var dateValidation = ValidateDateOptions(
            p.DueTime, p.DueEndTime, p.EndDate, p.FrequencyUnit, p.IsGeneral, p.DueDate);
        if (dateValidation is not null)
            return Result.Failure<Habit>(dateValidation);

        var reminderValidation = ValidateScheduledReminders(p.ScheduledReminders);
        if (reminderValidation is not null)
            return Result.Failure<Habit>(reminderValidation);

        // Note: fallback to UTC date is approximate -- used only for EndDate validation when
        // dueDate is null. The caller (CreateHabitCommand) resolves the correct local date,
        // so this path rarely fires and the 1-day drift is acceptable for a validation guard.
        var effectiveDueDate = p.DueDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        return Result.Success(new Habit
        {
            UserId = p.UserId,
            Title = p.Title.Trim(),
            Description = p.Description?.Trim(),
            FrequencyUnit = p.FrequencyUnit,
            FrequencyQuantity = p.FrequencyQuantity,
            Days = p.IsFlexible ? [] : (p.Days?.ToList() ?? []),
            IsBadHabit = p.IsBadHabit,
            IsGeneral = p.IsGeneral,
            IsFlexible = p.IsFlexible,
            DueDate = effectiveDueDate,
            // For monthly/yearly habits, capture the original anchor day so subsequent
            // advances can re-anchor through end-of-month clamps without drifting.
            OriginalDayOfMonth = p.FrequencyUnit is Enums.FrequencyUnit.Month or Enums.FrequencyUnit.Year
                ? effectiveDueDate.Day
                : null,
            DueTime = p.DueTime,
            DueEndTime = p.DueEndTime,
            ParentHabitId = p.ParentHabitId,
            ReminderEnabled = p.ReminderEnabled,
            ReminderTimes = p.ReminderTimes ?? [15],
            SlipAlertEnabled = p.SlipAlertEnabled,
            ChecklistItems = p.ChecklistItems ?? [],
            ScheduledReminders = p.ScheduledReminders ?? [],
            EndDate = p.EndDate,
            Position = p.Position,
            GoogleEventId = p.GoogleEventId,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result<HabitLog> Log(DateOnly date, string? note = null, bool advanceDueDate = true)
    {
        if (IsCompleted)
            return Result.Failure<HabitLog>("Cannot log a completed habit.");

        // Flexible and bad habits allow multiple logs per day; regular habits do not
        if (!IsBadHabit && !IsFlexible && _logs.Exists(l => l.Date == date))
            return Result.Failure<HabitLog>("This habit has already been logged for this date.");

        var log = HabitLog.Create(Id, date, 1, note);
        _logs.Add(log);

        // One-time task: mark as completed
        if (FrequencyUnit is null)
        {
            IsCompleted = true;
        }
        else if (!IsFlexible && advanceDueDate)
        {
            // Recurring (non-flexible) habit: advance DueDate past the logged date
            AdvanceDueDate(date);

            // Reset checklist for next occurrence
            if (ChecklistItems.Count > 0)
                ChecklistItems = ChecklistItems.Select(i => i with { IsChecked = false }).ToList();
        }

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(log);
    }

    public void AdvanceDueDate(DateOnly today)
    {
        do
        {
            var prev = DueDate;
            DueDate = AdvanceDueDateByOneStep();
            if (DueDate == prev) break;
        } while (DueDate <= today);

        // If the habit has run past its end date, mark it as completed
        if (EndDate.HasValue && DueDate > EndDate.Value)
        {
            IsCompleted = true;
        }

        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Advances DueDate to the nearest scheduled date on or after today, without going past it.
    /// Used by background service to keep DueDate current for recurring habits.
    /// </summary>
    public void CatchUpDueDate(DateOnly today)
    {
        while (DueDate < today && !IsCompleted)
        {
            var prev = DueDate;
            DueDate = AdvanceDueDateByOneStep();
            if (DueDate == prev) break;

            if (EndDate.HasValue && DueDate > EndDate.Value)
            {
                IsCompleted = true;
                break;
            }
        }

        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Advances DueDate by one frequency step, re-anchoring for monthly/yearly drift
    /// and snapping to the next matching day-of-week if Days are set.
    /// </summary>
    private DateOnly AdvanceDueDateByOneStep()
    {
        // Use the persisted OriginalDayOfMonth as the re-anchor target so day 31 stays
        // day 31 across the Feb clamp. Falls back to DueDate.Day for legacy rows that
        // pre-date the OriginalDayOfMonth migration.
        var originalDay = OriginalDayOfMonth ?? DueDate.Day;

        var next = (FrequencyUnit, FrequencyQuantity) switch
        {
            (Enums.FrequencyUnit.Day, var q) => DueDate.AddDays(q!.Value),
            (Enums.FrequencyUnit.Week, var q) => DueDate.AddDays(7 * q!.Value),
            (Enums.FrequencyUnit.Month, var q) => DueDate.AddMonths(q!.Value),
            (Enums.FrequencyUnit.Year, var q) => DueDate.AddYears(q!.Value),
            _ => DueDate
        };

        // Re-anchor monthly/yearly advances to the original day-of-month, clamped to the
        // last day of the target month. This prevents drift when AddMonths/AddYears clamps
        // (e.g., Jan 31 -> Feb 28 should re-anchor to Mar 31, not Mar 28).
        if (FrequencyUnit is Enums.FrequencyUnit.Month or Enums.FrequencyUnit.Year)
        {
            var daysInTargetMonth = DateTime.DaysInMonth(next.Year, next.Month);
            var correctedDay = Math.Min(originalDay, daysInTargetMonth);
            next = new DateOnly(next.Year, next.Month, correctedDay);
        }

        // If Days are specified, find the next matching day
        if (Days.Count > 0)
        {
            while (!Days.Contains(next.DayOfWeek))
                next = next.AddDays(1);
        }

        return next;
    }

    /// <summary>
    /// Advances DueDate past the current flexible window.
    /// Day=next day, Week=next Monday, Month=next 1st, Year=next Jan 1.
    /// </summary>
    public void AdvanceDueDatePastWindow(DateOnly today)
    {
        var windowEnd = FrequencyUnit switch
        {
            Enums.FrequencyUnit.Day => today,
            Enums.FrequencyUnit.Week => today.AddDays(6 - ((int)today.DayOfWeek + 6) % 7), // end of ISO week (Sunday)
            Enums.FrequencyUnit.Month => new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)),
            Enums.FrequencyUnit.Year => new DateOnly(today.Year, 12, 31),
            _ => today
        };
        DueDate = windowEnd.AddDays(1);
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Postpones a one-time task to the given date.
    /// </summary>
    public void PostponeTo(DateOnly date)
    {
        DueDate = date;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public Result<HabitLog> SkipFlexible(DateOnly date)
    {
        if (!IsFlexible)
            return Result.Failure<HabitLog>("Only flexible habits can be skipped this way.");

        if (FrequencyUnit is null)
            return Result.Failure<HabitLog>("Cannot skip a one-time task.");

        // Create a skip log (Value = 0 distinguishes from completion logs which use Value = 1)
        var log = HabitLog.Create(Id, date, 0, null);
        _logs.Add(log);
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(log);
    }

    public Result<HabitLog> Unlog(DateOnly date)
    {
        // Only match completion logs (Value > 0), not skip logs (Value == 0)
        var log = _logs.Find(l => l.Date == date && l.Value > 0);
        if (log is null)
            return Result.Failure<HabitLog>("No log found for this date.");

        _logs.Remove(log);

        if (FrequencyUnit is null)
        {
            IsCompleted = false;
        }
        else if (!IsFlexible)
        {
            DueDate = date;
        }

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(log);
    }

    public Result Update(HabitUpdateParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Title))
            return Result.Failure("Title is required.");

        var validationError = ValidateUpdateParams(p);
        if (validationError is not null)
            return Result.Failure(validationError);

        ApplyRequiredUpdates(p);
        ApplyOptionalUpdates(p);

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    private string? ValidateUpdateParams(HabitUpdateParams p)
    {
        var effectiveIsGeneral = p.IsGeneral ?? IsGeneral;
        var effectiveIsFlexible = p.IsFlexible ?? IsFlexible;

        var scheduleValidation = ValidateScheduleOptions(
            effectiveIsGeneral, effectiveIsFlexible, p.IsBadHabit, p.FrequencyUnit, p.FrequencyQuantity, p.Days);
        if (scheduleValidation is not null)
            return scheduleValidation;

        var effectiveDueEndTime = p.DueEndTime ?? DueEndTime;
        var effectiveDueTime = p.DueTime ?? DueTime;
        if (effectiveDueEndTime.HasValue && effectiveDueTime.HasValue && effectiveDueEndTime.Value <= effectiveDueTime.Value)
            return "End time must be after start time.";

        if (p.EndDate.HasValue && p.EndDate.Value < (p.DueDate ?? DueDate))
            return "End date must be on or after the start date.";

        return ValidateScheduledReminders(p.ScheduledReminders);
    }

    private void ApplyRequiredUpdates(HabitUpdateParams p)
    {
        var effectiveIsFlexible = p.IsFlexible ?? IsFlexible;

        Title = p.Title.Trim();
        Description = p.Description?.Trim();
        FrequencyUnit = p.FrequencyUnit;
        FrequencyQuantity = p.FrequencyQuantity;
        Days = effectiveIsFlexible ? [] : (p.Days?.ToList() ?? []);
        IsBadHabit = p.IsBadHabit;
        DueTime = p.DueTime;
        DueEndTime = p.DueEndTime;

        if (p.DueDate is not null)
            DueDate = p.DueDate.Value;

        // Re-anchor OriginalDayOfMonth on edit so changing a monthly habit from the 31st
        // to the 15th re-anchors to day 15 (instead of staying at the old day), and switching
        // a daily habit to monthly seeds the field instead of leaving it null.
        // Daily/weekly habits don't use this field, so clear it on transition away from
        // monthly/yearly to avoid stale anchors lingering across cadence flips.
        if (FrequencyUnit is Enums.FrequencyUnit.Month or Enums.FrequencyUnit.Year)
            OriginalDayOfMonth = DueDate.Day;
        else
            OriginalDayOfMonth = null;
    }

    private void ApplyOptionalUpdates(HabitUpdateParams p)
    {
        if (p.IsGeneral.HasValue)
            IsGeneral = p.IsGeneral.Value;
        if (p.IsFlexible.HasValue)
            IsFlexible = p.IsFlexible.Value;
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

        if (p.ClearEndDate == true)
            EndDate = null;
        else if (p.EndDate.HasValue)
            EndDate = p.EndDate.Value;
    }

    public void UpdateChecklist(IReadOnlyList<ChecklistItem> items)
    {
        ChecklistItems = items;
        UpdatedAtUtc = DateTime.UtcNow;
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
        ParentHabitId = parentHabitId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void AddTag(Tag tag) { if (!_tags.Contains(tag)) _tags.Add(tag); }

    public void RemoveTag(Tag tag) => _tags.Remove(tag);

    public void AddGoal(Goal goal) { if (!_goals.Contains(goal)) _goals.Add(goal); }

    public void RemoveGoal(Goal goal) => _goals.Remove(goal);

    // --- Validation helpers ---

    private static string? ValidateScheduleOptions(
        bool isGeneral, bool isFlexible, bool isBadHabit,
        FrequencyUnit? frequencyUnit, int? frequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? days)
    {
        if (isGeneral && (frequencyUnit is not null || frequencyQuantity is not null))
            return "General habits cannot have a frequency.";

        if (isGeneral && isBadHabit)
            return "General habits cannot be bad habits.";

        if (frequencyQuantity is not null && frequencyQuantity <= 0)
            return "Frequency quantity must be greater than 0.";

        if (isFlexible && frequencyUnit is null)
            return "Flexible habits must have a frequency unit.";

        if (isFlexible && days?.Count > 0)
            return "Flexible habits cannot have specific days set.";

        if (!isFlexible && days?.Count > 0 && frequencyQuantity != 1)
            return "Days can only be set when frequency quantity is 1.";

        return null;
    }

    private static string? ValidateDateOptions(
        TimeOnly? dueTime, TimeOnly? dueEndTime,
        DateOnly? endDate, FrequencyUnit? frequencyUnit,
        bool isGeneral, DateOnly? dueDate)
    {
        if (dueEndTime.HasValue && dueTime.HasValue && dueEndTime.Value <= dueTime.Value)
            return "End time must be after start time.";

        if (endDate.HasValue && frequencyUnit is null && !isGeneral)
            return "One-time tasks cannot have an end date.";

        var effectiveDueDate = dueDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (endDate.HasValue && endDate.Value < effectiveDueDate)
            return "End date must be on or after the start date.";

        return null;
    }

    private static string? ValidateScheduledReminders(
        IReadOnlyList<ScheduledReminderTime>? scheduledReminders)
    {
        if (scheduledReminders is null)
            return null;

        if (scheduledReminders.Count > DomainConstants.MaxScheduledReminders)
            return $"A habit can have at most {DomainConstants.MaxScheduledReminders} scheduled reminders.";

        var hasDuplicates = scheduledReminders
            .GroupBy(sr => (sr.When, sr.Time))
            .Any(g => g.Count() > 1);

        if (hasDuplicates)
            return "Scheduled reminders must not contain duplicate entries.";

        return null;
    }
}
