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
    public bool ReminderEnabled { get; private set; }
    public IReadOnlyList<int> ReminderTimes { get; private set; } = [15];
    public bool IsGeneral { get; private set; }
    public bool SlipAlertEnabled { get; private set; }
    public IReadOnlyList<ChecklistItem> ChecklistItems { get; private set; } = [];
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
        Guid? parentHabitId = null,
        bool reminderEnabled = false,
        IReadOnlyList<int>? reminderTimes = null,
        bool slipAlertEnabled = false,
        IReadOnlyList<ChecklistItem>? checklistItems = null,
        bool isGeneral = false)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Habit>("User ID is required.");

        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure<Habit>("Title is required.");

        if (isGeneral && (frequencyUnit is not null || frequencyQuantity is not null))
            return Result.Failure<Habit>("General habits cannot have a frequency.");

        if (frequencyQuantity is not null && frequencyQuantity <= 0)
            return Result.Failure<Habit>("Frequency quantity must be greater than 0.");

        if (days?.Count > 0 && frequencyQuantity != 1)
            return Result.Failure<Habit>("Days can only be set when frequency quantity is 1.");

        return Result.Success(new Habit
        {
            UserId = userId,
            Title = title.Trim(),
            Description = description?.Trim(),
            FrequencyUnit = frequencyUnit,
            FrequencyQuantity = frequencyQuantity,
            Days = days?.ToList() ?? [],
            IsBadHabit = isBadHabit,
            IsGeneral = isGeneral,
            DueDate = dueDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            DueTime = dueTime,
            ParentHabitId = parentHabitId,
            ReminderEnabled = reminderEnabled,
            ReminderTimes = reminderTimes ?? [15],
            SlipAlertEnabled = slipAlertEnabled,
            ChecklistItems = checklistItems ?? [],
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result<HabitLog> Log(DateOnly date, string? note = null)
    {
        if (IsCompleted)
            return Result.Failure<HabitLog>("Cannot log a completed habit.");

        if (!IsBadHabit && _logs.Exists(l => l.Date == date))
            return Result.Failure<HabitLog>("This habit has already been logged for this date.");

        var log = HabitLog.Create(Id, date, 1, note);
        _logs.Add(log);

        // One-time task: mark as completed
        if (FrequencyUnit is null)
        {
            IsCompleted = true;
        }
        else
        {
            // Recurring habit: advance DueDate past today
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
            DueDate = (FrequencyUnit, FrequencyQuantity) switch
            {
                (Enums.FrequencyUnit.Day, var q) => DueDate.AddDays(q!.Value),
                (Enums.FrequencyUnit.Week, var q) => DueDate.AddDays(7 * q!.Value),
                (Enums.FrequencyUnit.Month, var q) => DueDate.AddMonths(q!.Value),
                (Enums.FrequencyUnit.Year, var q) => DueDate.AddYears(q!.Value),
                _ => DueDate
            };

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
    }

    public Result<HabitLog> Unlog(DateOnly date)
    {
        var log = _logs.Find(l => l.Date == date);
        if (log is null)
            return Result.Failure<HabitLog>("No log found for this date.");

        _logs.Remove(log);

        if (FrequencyUnit is null)
        {
            IsCompleted = false;
        }
        else
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
        bool? reminderEnabled = null,
        IReadOnlyList<int>? reminderTimes = null,
        bool? slipAlertEnabled = null,
        IReadOnlyList<ChecklistItem>? checklistItems = null,
        bool? isGeneral = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure("Title is required.");

        var effectiveIsGeneral = isGeneral ?? IsGeneral;
        if (effectiveIsGeneral && (frequencyUnit is not null || frequencyQuantity is not null))
            return Result.Failure("General habits cannot have a frequency.");

        if (frequencyQuantity is not null && frequencyQuantity <= 0)
            return Result.Failure("Frequency quantity must be greater than 0.");

        if (days?.Count > 0 && frequencyQuantity != 1)
            return Result.Failure("Days can only be set when frequency quantity is 1.");

        Title = title.Trim();
        Description = description?.Trim();
        FrequencyUnit = frequencyUnit;
        FrequencyQuantity = frequencyQuantity;
        Days = days?.ToList() ?? [];
        IsBadHabit = isBadHabit;

        if (isGeneral.HasValue)
            IsGeneral = isGeneral.Value;

        if (dueDate is not null)
            DueDate = dueDate.Value;

        DueTime = dueTime;

        if (reminderEnabled.HasValue)
            ReminderEnabled = reminderEnabled.Value;
        if (reminderTimes is not null)
            ReminderTimes = reminderTimes;
        if (slipAlertEnabled.HasValue)
            SlipAlertEnabled = slipAlertEnabled.Value;
        if (checklistItems is not null)
            ChecklistItems = checklistItems;

        return Result.Success();
    }

    public void UpdateChecklist(IReadOnlyList<ChecklistItem> items) => ChecklistItems = items;

    public void SetPosition(int? position) => Position = position;

    public void SetParentHabitId(Guid? parentHabitId) => ParentHabitId = parentHabitId;

    public void AddTag(Tag tag) { if (!_tags.Contains(tag)) _tags.Add(tag); }

    public void RemoveTag(Tag tag) => _tags.Remove(tag);

}
