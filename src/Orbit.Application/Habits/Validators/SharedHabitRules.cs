using FluentValidation;
using Orbit.Application.Common;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Validators;

public static class SharedHabitRules
{
    public static void AddRepeatReminderRules<T>(IRuleBuilder<T, RepeatReminderOptions?> rule) =>
        rule.Must(repeat => repeat is null || repeat.IsValid())
            .WithCopy(Orbit.Domain.Common.DomainErrors.InvalidRepeatReminder.Code);

    public static void AddTitleRules<T>(
        IRuleBuilder<T, string> rule,
        string? requiredCode = null,
        string? maximumLengthCode = null)
    {
        var requiredRule = rule.NotEmpty();
        if (requiredCode is not null)
            requiredRule.WithCopy(requiredCode);
        else
            requiredRule.WithFieldCopy(ValidationCopyKeys.HabitTitleRequired);

        var lengthRule = requiredRule.MaximumLength(AppConstants.MaxHabitTitleLength);
        if (maximumLengthCode is not null)
            lengthRule.WithCopy(maximumLengthCode);
        else
            lengthRule.WithFieldCopy(ValidationCopyKeys.HabitTitleLength);
    }

    public static void AddPolarityRules<T>(AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, string>> title, Func<T, bool> isBadHabit)
    {
        validator.RuleFor(title)
            .Must((item, name) => Domain.Entities.HabitSemantics.ValidatePolarity(name, isBadHabit(item)) is null)
            .WithMessage(Domain.Common.DomainErrors.BadHabitNegatedName.Message)
            .WithErrorCode(Domain.Common.DomainErrors.BadHabitNegatedName.Code);
    }

    public static void AddDescriptionRules<T>(IRuleBuilder<T, string?> rule, bool isChild = false)
    {
        rule.MaximumLength(AppConstants.MaxHabitDescriptionLength)
            .WithFieldCopy(isChild ? ValidationCopyKeys.SubHabitDescriptionLength : ValidationCopyKeys.HabitDescriptionLength);
    }

    public static void AddEmojiRules<T>(IRuleBuilder<T, string?> rule, bool isChild = false)
    {
        rule.MaximumLength(AppConstants.MaxHabitEmojiLength)
            .WithFieldCopy(isChild ? ValidationCopyKeys.SubHabitEmojiLength : ValidationCopyKeys.HabitEmojiLength);
    }

    public static void AddChecklistItemRules<T>(IRuleBuilder<T, IReadOnlyList<ChecklistItem>?> rule)
    {
        rule.Must(items => items is null || items.All(i => i.Text.Length <= AppConstants.MaxChecklistItemTextLength))
            .WithCopy(ValidationErrorCodes.ChecklistItemLength);
    }

    public static void AddFrequencyQuantityRules<T>(IRuleBuilderOptions<T, int?> rule)
    {
        rule.GreaterThan(0).WithFieldCopy(ValidationCopyKeys.FrequencyPositive);
    }

    public static void AddIntervalWeeksRules<T>(IRuleBuilder<T, int?> rule)
    {
        rule.InclusiveBetween(1, AppConstants.MaxIntervalWeeks);
    }

    /// <summary>
    /// Month days are distinct days from 1 to 31 on a monthly, non-flexible habit with no weekdays.
    /// An empty list is valid only where it clears stored days (<paramref name="allowEmpty"/>).
    /// </summary>
    public static void AddMonthDaysRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<int>?>> monthDaysExpr,
        Func<T, Domain.Enums.FrequencyUnit?> freqUnit,
        Func<T, bool> isFlexible,
        Func<T, IReadOnlyList<System.DayOfWeek>?> days,
        bool allowEmpty)
    {
        AddMonthDaysRangeRules(validator.RuleFor(monthDaysExpr), allowEmpty);

        validator.RuleFor(monthDaysExpr)
            .Must((command, monthDays) => monthDays is not { Count: > 0 }
                || (freqUnit(command) == Domain.Enums.FrequencyUnit.Month && !isFlexible(command)
                    && days(command) is not { Count: > 0 }))
            .WithCopy(Domain.Common.DomainErrors.MonthDaysRequireMonth.Code);
    }

    /// <summary>
    /// The range and duplicate rule alone, for an item whose frequency unit can come from its parent.
    /// The domain guard checks the effective unit.
    /// </summary>
    public static void AddMonthDaysRangeRules<T>(IRuleBuilder<T, IReadOnlyList<int>?> rule, bool allowEmpty)
    {
        rule.Must(monthDays => monthDays is null
                || (monthDays.Count == 0 ? allowEmpty : Domain.Common.MonthDaySchedule.IsValid(monthDays)))
            .WithCopy(Domain.Common.DomainErrors.MonthDaysInvalid.Code);
    }

    public static void AddDaysRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<System.DayOfWeek>?>> daysExpr,
        System.Linq.Expressions.Expression<Func<T, int?>> freqQtyExpr,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr,
        System.Linq.Expressions.Expression<Func<T, bool>> isFlexibleExpr)
    {
        var freqQtyFunc = freqQtyExpr.Compile();
        var freqUnitFunc = freqUnitExpr.Compile();
        var isFlexibleFunc = isFlexibleExpr.Compile();

        validator.RuleFor(daysExpr)
            .Must((command, days) =>
            {
                if (days is null || days.Count == 0) return true;
                if (isFlexibleFunc(command)) return true;
                return freqQtyFunc(command) == 1 && freqUnitFunc(command) == Domain.Enums.FrequencyUnit.Day;
            })
            .WithCopy(ValidationErrorCodes.DaysDailyOnly);
    }

    public static void AddOneTimeTaskEndDateRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, DateOnly?>> endDateExpr,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr)
    {
        var freqUnitFunc = freqUnitExpr.Compile();
        validator.RuleFor(endDateExpr)
            .Must((command, endDate) =>
                !endDate.HasValue || freqUnitFunc(command) is not null)
            .WithCopy(ValidationErrorCodes.EndDateRecurringOnly);
    }

    public static void AddGeneralHabitRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, bool>> isGeneralExpr,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr,
        System.Linq.Expressions.Expression<Func<T, int?>> freqQtyExpr,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<System.DayOfWeek>?>> daysExpr)
    {
        var isGeneralFunc = isGeneralExpr.Compile();
        AddGeneralHabitRulesCore(validator, x => isGeneralFunc(x), freqUnitExpr, freqQtyExpr, daysExpr);
    }

    public static void AddGeneralHabitRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, bool?>> isGeneralExpr,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr,
        System.Linq.Expressions.Expression<Func<T, int?>> freqQtyExpr,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<System.DayOfWeek>?>> daysExpr)
    {
        var isGeneralFunc = isGeneralExpr.Compile();
        AddGeneralHabitRulesCore(validator, x => isGeneralFunc(x) == true, freqUnitExpr, freqQtyExpr, daysExpr);
    }

    public static void AddScheduledReminderRules<T>(IRuleBuilder<T, IReadOnlyList<ScheduledReminderTime>?> rule)
    {
        rule.Must(items => items is null || items.Count <= AppConstants.MaxScheduledReminders)
            .WithCopy(ValidationErrorCodes.ScheduledReminderLimit);

        rule.Must(items => items is null || items.All(sr => Enum.IsDefined(sr.When)))
            .WithCopy(ValidationErrorCodes.ScheduledReminderWhen);

        rule.Must(items =>
            {
                if (items is null) return true;
                var grouped = items.GroupBy(sr => (sr.When, sr.Time));
                return grouped.All(g => g.Count() == 1);
            })
            .WithCopy(ValidationErrorCodes.ScheduledRemindersUnique);
    }

    public static void AddReminderTimesRules<T>(IRuleBuilder<T, IReadOnlyList<int>?> rule)
    {
        rule.Must(times => times is null || times.Count <= AppConstants.MaxReminderTimes)
            .WithCopy(ValidationErrorCodes.ReminderTimeLimit);

        rule.Must(times => times is null || times.All(m => m is >= 0 and <= AppConstants.MaxReminderMinutesBefore))
            .WithCopy(ValidationErrorCodes.ReminderTimeRange);

        rule.Must(times => times is null || times.Distinct().Count() == times.Count)
            .WithCopy(ValidationErrorCodes.ReminderTimesUnique);
    }

    public static void AddRelativeReminderRules<T>(IRuleBuilder<T, IReadOnlyList<RelativeReminderTime>?> rule)
    {
        rule.Must(reminders => reminders is null ||
            (reminders.Count <= AppConstants.MaxRelativeReminders
            && reminders.Distinct().Count() == reminders.Count
            && reminders.All(r => (r.MinutesBefore.HasValue != r.When.HasValue)
                && (r.When.HasValue == r.Time.HasValue)
                && (!r.MinutesBefore.HasValue || r.MinutesBefore.Value is >= -1439 and <= AppConstants.MaxReminderMinutesBefore)
                && (!r.When.HasValue || Enum.IsDefined(r.When.Value)))))
            .WithCopy(ValidationErrorCodes.RelativeRemindersValid);
    }

    public static void AddGoalIdsRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<Guid>?>> expression)
    {
        validator.RuleFor(expression)
            .Must(ids => ids is null || ids.Count <= AppConstants.MaxGoalsPerHabit)
            .WithCopy(ValidationErrorCodes.HabitGoalLimit);
    }

    private static void AddGeneralHabitRulesCore<T>(
        AbstractValidator<T> validator,
        Func<T, bool> isGeneralPredicate,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr,
        System.Linq.Expressions.Expression<Func<T, int?>> freqQtyExpr,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<System.DayOfWeek>?>> daysExpr)
    {
        validator.RuleFor(freqUnitExpr)
            .Null()
            .When(isGeneralPredicate)
            .WithCopy(ValidationErrorCodes.GeneralFrequencyUnitAbsent);

        validator.RuleFor(freqQtyExpr)
            .Null()
            .When(isGeneralPredicate)
            .WithCopy(ValidationErrorCodes.GeneralFrequencyQuantityAbsent);

        validator.RuleFor(daysExpr)
            .Must(days => days is null || days.Count == 0)
            .When(isGeneralPredicate)
            .WithCopy(ValidationErrorCodes.GeneralDaysAbsent);
    }
}
