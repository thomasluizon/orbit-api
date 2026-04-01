using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Entities;

public class Habit : Entity
{
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public FrequencyUnit? FrequencyUnit { get; private set; }
    public int? FrequencyQuantity { get; private set; }
    public bool IsBadHabit { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateOnly DueDate { get; private set; }
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
    public DateTime CreatedAtUtc { get; private set; }
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

    public static Result<Habit> Create(
        Guid userId,
        string title,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        string? description = null,
        IReadOnlyList<System.DayOfWeek>? days = null,
        bool isBadHabit = false,
        DateOnly? dueDate = null,
        TimeOnly? dueTime = null,
        TimeOnly? dueEndTime = null,
        Guid? parentHabitId = null,
        bool reminderEnabled = false,
        IReadOnlyList<int>? reminderTimes = null,
        bool slipAlertEnabled = false,
        IReadOnlyList<ChecklistItem>? checklistItems = null,
        bool isGeneral = false,
        bool isFlexible = false,
        DateOnly? endDate = null,
        IReadOnlyList<ScheduledReminderTime>? scheduledReminders = null)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Habit>("User ID is required.");

        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure<Habit>("Title is required.");

        if (isGeneral && (frequencyUnit is not null || frequencyQuantity is not null))
            return Result.Failure<Habit>("General habits cannot have a frequency.");

        if (isGeneral && isBadHabit)
            return Result.Failure<Habit>("General habits cannot be bad habits.");

        if (frequencyQuantity is not null && frequencyQuantity <= 0)
            return Result.Failure<Habit>("Frequency quantity must be greater than 0.");

        if (isFlexible && frequencyUnit is null)
            return Result.Failure<Habit>("Flexible habits must have a frequency unit.");

        if (isFlexible && days?.Count > 0)
            return Result.Failure<Habit>("Flexible habits cannot have specific days set.");

        if (days?.Count > 0 && frequencyQuantity != 1)
            return Result.Failure<Habit>("Days can only be set when frequency quantity is 1.");

        if (dueEndTime.HasValue && dueTime.HasValue && dueEndTime.Value <= dueTime.Value)
            return Result.Failure<Habit>("End time must be after start time.");

        if (endDate.HasValue && frequencyUnit is null && !isGeneral)
            return Result.Failure<Habit>("One-time tasks cannot have an end date.");

        // Note: fallback to UTC date is approximate -- used only for EndDate validation when
        // dueDate is null. The caller (CreateHabitCommand) resolves the correct local date,
        // so this path rarely fires and the 1-day drift is acceptable for a validation guard.
        var effectiveDueDate = dueDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (endDate.HasValue && endDate.Value < effectiveDueDate)
            return Result.Failure<Habit>("End date must be on or after the start date.");

        if (scheduledReminders is not null && scheduledReminders.Count > DomainConstants.MaxScheduledReminders)
            return Result.Failure<Habit>($"A habit can have at most {DomainConstants.MaxScheduledReminders} scheduled reminders.");

        if (scheduledReminders is not null)
        {

            var hasDuplicates = scheduledReminders
                .GroupBy(sr => (sr.When, sr.Time))
                .Any(g => g.Count() > 1);
            if (hasDuplicates)
                return Result.Failure<Habit>("Scheduled reminders must not contain duplicate entries.");
        }

        return Result.Success(new Habit
        {
            UserId = userId,
            Title = title.Trim(),
            Description = description?.Trim(),
            FrequencyUnit = frequencyUnit,
            FrequencyQuantity = frequencyQuantity,
            Days = isFlexible ? [] : (days?.ToList() ?? []),
            IsBadHabit = isBadHabit,
            IsGeneral = isGeneral,
            IsFlexible = isFlexible,
            DueDate = effectiveDueDate,
            DueTime = dueTime,
            DueEndTime = dueEndTime,
            ParentHabitId = parentHabitId,
            ReminderEnabled = reminderEnabled,
            ReminderTimes = reminderTimes ?? [15],
            SlipAlertEnabled = slipAlertEnabled,
            ChecklistItems = checklistItems ?? [],
            ScheduledReminders = scheduledReminders ?? [],
            EndDate = endDate,
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

        return Result.Success(log);
    }

    public void AdvanceDueDate(DateOnly today)
    {
        do
        {
            var prev = DueDate;
            // Preserve the original day-of-month/year before advancing, so monthly habits
            // anchored on day 29/30/31 don't drift when a short month clamps them (e.g.,
            // Jan 31 -> Feb 28 -> Mar 28 instead of the correct Mar 31).
            var originalDay = DueDate.Day;

            DueDate = (FrequencyUnit, FrequencyQuantity) switch
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
                var daysInTargetMonth = DateTime.DaysInMonth(DueDate.Year, DueDate.Month);
                var correctedDay = Math.Min(originalDay, daysInTargetMonth);
                DueDate = new DateOnly(DueDate.Year, DueDate.Month, correctedDay);
            }

            // If Days are specified, find the next matching day
            if (Days.Count > 0)
            {
                while (!Days.Contains(DueDate.DayOfWeek))
                {
                    DueDate = DueDate.AddDays(1);
                }
            }

            if (DueDate == prev) break;
        } while (DueDate <= today);

        // If the habit has run past its end date, mark it as completed
        if (EndDate.HasValue && DueDate > EndDate.Value)
        {
            IsCompleted = true;
        }
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
            var originalDay = DueDate.Day;

            DueDate = (FrequencyUnit, FrequencyQuantity) switch
            {
                (Enums.FrequencyUnit.Day, var q) => DueDate.AddDays(q!.Value),
                (Enums.FrequencyUnit.Week, var q) => DueDate.AddDays(7 * q!.Value),
                (Enums.FrequencyUnit.Month, var q) => DueDate.AddMonths(q!.Value),
                (Enums.FrequencyUnit.Year, var q) => DueDate.AddYears(q!.Value),
                _ => DueDate
            };

            // Re-anchor to original day-of-month to prevent drift on short months
            if (FrequencyUnit is Enums.FrequencyUnit.Month or Enums.FrequencyUnit.Year)
            {
                var daysInTargetMonth = DateTime.DaysInMonth(DueDate.Year, DueDate.Month);
                var correctedDay = Math.Min(originalDay, daysInTargetMonth);
                DueDate = new DateOnly(DueDate.Year, DueDate.Month, correctedDay);
            }

            if (Days.Count > 0)
            {
                while (!Days.Contains(DueDate.DayOfWeek))
                    DueDate = DueDate.AddDays(1);
            }

            if (DueDate == prev) break;

            if (EndDate.HasValue && DueDate > EndDate.Value)
            {
                IsCompleted = true;
                break;
            }
        }
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
    }

    /// <summary>
    /// Postpones a one-time task to the given date.
    /// </summary>
    public void PostponeTo(DateOnly date)
    {
        DueDate = date;
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

        return Result.Success(log);
    }

    public Result Update(
        string title,
        string? description,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? days,
        bool isBadHabit,
        DateOnly? dueDate,
        TimeOnly? dueTime = null,
        TimeOnly? dueEndTime = null,
        bool? reminderEnabled = null,
        IReadOnlyList<int>? reminderTimes = null,
        bool? slipAlertEnabled = null,
        IReadOnlyList<ChecklistItem>? checklistItems = null,
        bool? isGeneral = null,
        bool? isFlexible = null,
        DateOnly? endDate = null,
        bool? clearEndDate = null,
        IReadOnlyList<ScheduledReminderTime>? scheduledReminders = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure("Title is required.");

        var effectiveIsGeneral = isGeneral ?? IsGeneral;
        if (effectiveIsGeneral && (frequencyUnit is not null || frequencyQuantity is not null))
            return Result.Failure("General habits cannot have a frequency.");

        if (effectiveIsGeneral && isBadHabit)
            return Result.Failure("General habits cannot be bad habits.");

        if (frequencyQuantity is not null && frequencyQuantity <= 0)
            return Result.Failure("Frequency quantity must be greater than 0.");

        var effectiveIsFlexible = isFlexible ?? IsFlexible;

        if (effectiveIsFlexible && frequencyUnit is null)
            return Result.Failure("Flexible habits must have a frequency unit.");

        if (effectiveIsFlexible && days?.Count > 0)
            return Result.Failure("Flexible habits cannot have specific days set.");

        if (!effectiveIsFlexible && days?.Count > 0 && frequencyQuantity != 1)
            return Result.Failure("Days can only be set when frequency quantity is 1.");

        var effectiveDueEndTime = dueEndTime ?? DueEndTime;
        var effectiveDueTime = dueTime ?? DueTime;
        if (effectiveDueEndTime.HasValue && effectiveDueTime.HasValue && effectiveDueEndTime.Value <= effectiveDueTime.Value)
            return Result.Failure("End time must be after start time.");

        // Validate endDate if being set
        if (endDate.HasValue)
        {
            var effectiveDueDate = dueDate ?? DueDate;
            if (endDate.Value < effectiveDueDate)
                return Result.Failure("End date must be on or after the start date.");
        }

        Title = title.Trim();
        Description = description?.Trim();
        FrequencyUnit = frequencyUnit;
        FrequencyQuantity = frequencyQuantity;
        Days = effectiveIsFlexible ? [] : (days?.ToList() ?? []);
        IsBadHabit = isBadHabit;

        if (isGeneral.HasValue)
            IsGeneral = isGeneral.Value;
        if (isFlexible.HasValue)
            IsFlexible = isFlexible.Value;

        if (dueDate is not null)
            DueDate = dueDate.Value;

        DueTime = dueTime;
        DueEndTime = dueEndTime;

        if (reminderEnabled.HasValue)
            ReminderEnabled = reminderEnabled.Value;
        if (reminderTimes is not null)
            ReminderTimes = reminderTimes;
        if (slipAlertEnabled.HasValue)
            SlipAlertEnabled = slipAlertEnabled.Value;
        if (checklistItems is not null)
            ChecklistItems = checklistItems;

        if (scheduledReminders is not null && scheduledReminders.Count > DomainConstants.MaxScheduledReminders)
            return Result.Failure($"A habit can have at most {DomainConstants.MaxScheduledReminders} scheduled reminders.");

        if (scheduledReminders is not null)
        {

            var hasDuplicates = scheduledReminders
                .GroupBy(sr => (sr.When, sr.Time))
                .Any(g => g.Count() > 1);
            if (hasDuplicates)
                return Result.Failure("Scheduled reminders must not contain duplicate entries.");

            ScheduledReminders = scheduledReminders;
        }

        if (clearEndDate == true)
            EndDate = null;
        else if (endDate.HasValue)
            EndDate = endDate.Value;

        return Result.Success();
    }

    public void UpdateChecklist(IReadOnlyList<ChecklistItem> items) => ChecklistItems = items;

    public void SetPosition(int? position) => Position = position;

    public void SetParentHabitId(Guid? parentHabitId) => ParentHabitId = parentHabitId;

    public void AddTag(Tag tag) { if (!_tags.Contains(tag)) _tags.Add(tag); }

    public void RemoveTag(Tag tag) => _tags.Remove(tag);

    public void AddGoal(Goal goal) { if (!_goals.Contains(goal)) _goals.Add(goal); }

    public void RemoveGoal(Goal goal) => _goals.Remove(goal);

}
