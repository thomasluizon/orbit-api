using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Validators;

public class UpdateHabitCommandValidator : AbstractValidator<UpdateHabitCommand>
{
    private static readonly FieldsValidator HabitFieldsValidator = new(false);
    private static readonly FieldsValidator SubHabitFieldsValidator = new(true);

    public UpdateHabitCommandValidator(IGenericRepository<Habit> habitRepository)
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x.ParentId)
            .NotEqual(Guid.Empty)
            .WithCopy(ErrorCodes.TargetParentNotFound)
            .When(x => x.ParentId.HasValue);

        RuleFor(x => x.ParentId)
            .Must((command, parentId) => parentId != command.HabitId)
            .WithCopy(ErrorCodes.SelfParent)
            .When(x => x.ParentId.HasValue);

        RuleFor(x => x.ParentId)
            .Must((command, parentId) => command.ClearParent != true || parentId is null)
            .WithCopy(ErrorCodes.ValidationError);

        RuleFor(x => x).CustomAsync(async (command, context, cancellationToken) =>
        {
            var title = command.Title;
            var isEmpty = string.IsNullOrWhiteSpace(title);
            if (!isEmpty && title.Length <= AppConstants.MaxHabitTitleLength
                && (command.Description?.Length ?? 0) <= AppConstants.MaxHabitDescriptionLength
                && (command.Emoji?.Length ?? 0) <= AppConstants.MaxHabitEmojiLength)
                return;

            var habit = await habitRepository.FindOneTrackedAsync(
                h => h.Id == command.HabitId && h.UserId == command.UserId,
                cancellationToken: cancellationToken);
            var fieldsValidator = habit?.ParentHabitId is not null
                ? SubHabitFieldsValidator
                : HabitFieldsValidator;
            foreach (var failure in fieldsValidator.Validate(command).Errors)
                context.AddFailure(failure);
        });

        When(x => x.Options is not null, () =>
        {
            SharedHabitRules.AddChecklistItemRules(RuleFor(x => x.Options!.ChecklistItems));
            SharedHabitRules.AddRepeatReminderRules(RuleFor(x => x.Options!.RepeatReminder));
        });

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .WithFieldCopy(ValidationCopyKeys.FrequencyPositive)
            .When(x => x.FrequencyQuantity is not null);

        RuleFor(x => x.FrequencyQuantity)
            .NotNull()
            .WithCopy(ValidationErrorCodes.FrequencyQuantityRequired)
            .When(x => x.FrequencyUnit is not null);

        RuleFor(x => x.StartDate)
            .Must((x, date) => !date.HasValue || (date != DateOnly.MinValue
                && !(x.IsGeneral == true) && x.FrequencyUnit.HasValue))
            .WithCopy(ValidationErrorCodes.CalendarDateValid);
        RuleFor(x => x.StartDate)
            .Must((x, date) => !date.HasValue || x.Options?.EndDate is not { } end || end >= date.Value)
            .WithCopy(ValidationErrorCodes.DateRangeOrderLower);

        SharedHabitRules.AddIntervalWeeksRules(RuleFor(x => x.IntervalWeeks));

        SharedHabitRules.AddDaysRules(this,
            x => x.Options != null ? x.Options.Days : null,
            x => x.FrequencyQuantity,
            x => x.FrequencyUnit,
            x => x.Options != null && x.Options.IsFlexible == true);

        SharedHabitRules.AddOneTimeTaskEndDateRules(this,
            x => x.Options != null ? x.Options.EndDate : null,
            x => x.FrequencyUnit);

        SharedHabitRules.AddGeneralHabitRules(this,
            x => x.IsGeneral,
            x => x.FrequencyUnit,
            x => x.FrequencyQuantity,
            x => x.Options != null ? x.Options.Days : null);

        RuleFor(x => x.IsBadHabit)
            .Equal(false)
            .When(x => x.IsGeneral == true)
            .WithCopy(ValidationErrorCodes.GeneralHabitNotBad);

        When(x => x.Options is not null, () =>
        {
            SharedHabitRules.AddScheduledReminderRules(RuleFor(x => x.Options!.ScheduledReminders));

            SharedHabitRules.AddReminderTimesRules(RuleFor(x => x.Options!.ReminderTimes));
            SharedHabitRules.AddRelativeReminderRules(RuleFor(x => x.Options!.RelativeReminders));
        });

        SharedHabitRules.AddGoalIdsRules(this, x => x.GoalIds);

        RuleFor(x => x.TagIds)
            .Must(ids => ids is null || ids.Count <= AppConstants.MaxTagsPerHabit)
            .WithCopy(ValidationErrorCodes.HabitTagLimit);

        RuleForEach(x => x.TagIds).NotEmpty();
    }

    private sealed class FieldsValidator : AbstractValidator<UpdateHabitCommand>
    {
        public FieldsValidator(bool isChild)
        {
            SharedHabitRules.AddTitleRules(
                RuleFor(x => x.Title),
                requiredCode: isChild ? ValidationErrorCodes.SubHabitTitleRequired : null,
                maximumLengthCode: isChild
                    ? ValidationErrorCodes.SubHabitTitleLength
                    : null);
            SharedHabitRules.AddPolarityRules(this, x => x.Title, x => x.IsBadHabit);
            SharedHabitRules.AddDescriptionRules(RuleFor(x => x.Description), isChild);
            SharedHabitRules.AddEmojiRules(RuleFor(x => x.Emoji), isChild);
        }
    }
}
