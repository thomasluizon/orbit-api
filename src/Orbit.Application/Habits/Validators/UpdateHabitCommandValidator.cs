using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class UpdateHabitCommandValidator : AbstractValidator<UpdateHabitCommand>
{
    public UpdateHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        SharedHabitRules.AddTitleRules(RuleFor(x => x.Title));

        SharedHabitRules.AddDescriptionRules(RuleFor(x => x.Description));

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

        SharedHabitRules.AddDaysRules(this, x => x.Options != null ? x.Options.Days : null, x => x.FrequencyQuantity);

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
        });

        SharedHabitRules.AddGoalIdsRules(this, x => x.GoalIds);
    }
}
