using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class ApplyHabitInputValidator : AbstractValidator<ApplyHabitInput>
{
    public ApplyHabitInputValidator()
    {
        SharedHabitRules.AddTitleRules(RuleFor(x => x.Title));
        SharedHabitRules.AddDescriptionRules(RuleFor(x => x.Description));
        SharedHabitRules.AddEmojiRules(RuleFor(x => x.Emoji));
        SharedHabitRules.AddChecklistItemRules(RuleFor(x => x.ChecklistItems));

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .When(x => x.FrequencyQuantity is not null);

        RuleFor(x => x.FrequencyQuantity)
            .NotNull()
            .WithMessage("Frequency quantity is required when frequency unit is set")
            .When(x => x.FrequencyUnit is not null);

        SharedHabitRules.AddReminderTimesRules(RuleFor(x => x.ReminderTimes));
        SharedHabitRules.AddDaysRules(this, x => x.Days, x => x.FrequencyQuantity, x => x.FrequencyUnit, x => x.IsFlexible);
        SharedHabitRules.AddGeneralHabitRules(this, x => x.IsGeneral, x => x.FrequencyUnit, x => x.FrequencyQuantity, x => x.Days);
    }
}

public class ApplyGoalInputValidator : AbstractValidator<ApplyGoalInput>
{
    public ApplyGoalInputValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(AppConstants.MaxGoalDescriptionLength);
        RuleFor(x => x.TargetValue).GreaterThan(0);
        RuleFor(x => x.Unit).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Type).IsInEnum();
    }
}

public class ApplyOnboardingCommandValidator : AbstractValidator<ApplyOnboardingCommand>
{
    public ApplyOnboardingCommandValidator()
    {
        RuleFor(x => x.Habits).NotNull();
        RuleFor(x => x.Habits.Count).LessThanOrEqualTo(AppConstants.MaxBulkOperationSize)
            .When(x => x.Habits is not null);
        RuleForEach(x => x.Habits).SetValidator(new ApplyHabitInputValidator());

        When(x => x.Goal is not null, () =>
            RuleFor(x => x.Goal!).SetValidator(new ApplyGoalInputValidator()));

        When(x => x.WeekStartDay is not null, () =>
            RuleFor(x => x.WeekStartDay!.Value).Must(day => day is 0 or 1)
                .WithMessage("Week start day must be 0 (Sunday) or 1 (Monday)."));
    }
}
