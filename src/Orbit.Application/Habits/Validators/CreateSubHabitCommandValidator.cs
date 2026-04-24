using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class CreateSubHabitCommandValidator : AbstractValidator<CreateSubHabitCommand>
{
    public CreateSubHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.ParentHabitId)
            .NotEmpty();

        SharedHabitRules.AddTitleRules(RuleFor(x => x.Title));

        SharedHabitRules.AddDescriptionRules(RuleFor(x => x.Description));

        SharedHabitRules.AddEmojiRules(RuleFor(x => x.Emoji));

        SharedHabitRules.AddChecklistItemRules(RuleFor(x => x.Options != null ? x.Options.ChecklistItems : null));

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .When(x => x.FrequencyQuantity is not null);

        SharedHabitRules.AddDaysRules(this, x => x.Options != null ? x.Options.Days : null, x => x.FrequencyQuantity);

        SharedHabitRules.AddScheduledReminderRules(RuleFor(x => x.Options != null ? x.Options.ScheduledReminders : null));

        RuleFor(x => x.TagIds)
            .Must(tags => tags is null || tags.Count <= AppConstants.MaxTagsPerHabit)
            .WithMessage($"A habit can have at most {AppConstants.MaxTagsPerHabit} tags");
    }
}
