using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Validators;

public class UpdateHabitCommandValidator : AbstractValidator<UpdateHabitCommand>
{
    private static readonly TitleValidator HabitTitleValidator = new(false);
    private static readonly TitleValidator SubHabitTitleValidator = new(true);

    public UpdateHabitCommandValidator(IGenericRepository<Habit> habitRepository)
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x).CustomAsync(async (command, context, cancellationToken) =>
        {
            var title = command.Title;
            var isEmpty = string.IsNullOrWhiteSpace(title);
            if (!isEmpty && title.Length <= AppConstants.MaxHabitTitleLength)
                return;

            var habit = await habitRepository.FindOneTrackedAsync(
                h => h.Id == command.HabitId && h.UserId == command.UserId,
                cancellationToken: cancellationToken);
            var titleValidator = habit?.ParentHabitId is not null
                ? SubHabitTitleValidator
                : HabitTitleValidator;
            foreach (var failure in titleValidator.Validate(command).Errors)
                context.AddFailure(failure);
        });

        SharedHabitRules.AddDescriptionRules(RuleFor(x => x.Description));

        SharedHabitRules.AddEmojiRules(RuleFor(x => x.Emoji));

        When(x => x.Options is not null, () =>
        {
            SharedHabitRules.AddChecklistItemRules(RuleFor(x => x.Options!.ChecklistItems));
        });

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .When(x => x.FrequencyQuantity is not null);

        RuleFor(x => x.FrequencyQuantity)
            .NotNull()
            .WithMessage("Frequency quantity is required when frequency unit is set")
            .When(x => x.FrequencyUnit is not null);

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
            .WithMessage("General habits cannot be bad habits");

        When(x => x.Options is not null, () =>
        {
            SharedHabitRules.AddScheduledReminderRules(RuleFor(x => x.Options!.ScheduledReminders));

            SharedHabitRules.AddReminderTimesRules(RuleFor(x => x.Options!.ReminderTimes));
        });

        SharedHabitRules.AddGoalIdsRules(this, x => x.GoalIds);
    }

    private sealed class TitleValidator : AbstractValidator<UpdateHabitCommand>
    {
        public TitleValidator(bool isChild)
        {
            SharedHabitRules.AddTitleRules(
                RuleFor(x => x.Title),
                requiredMessage: isChild ? "Sub-habit title must not be empty" : null,
                maximumLengthMessage: isChild
                    ? $"Sub-habit title must not exceed {AppConstants.MaxHabitTitleLength} characters"
                    : null);
        }
    }
}
