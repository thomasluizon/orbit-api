using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Social.Commands;

namespace Orbit.Application.Social.Validators;

public class SendCheerCommandValidator : AbstractValidator<SendCheerCommand>
{
    public SendCheerCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.RecipientId).NotEmpty().NotEqual(x => x.UserId);
        RuleFor(x => x.HabitId).NotEmpty();
        RuleFor(x => x.Note).MaximumLength(AppConstants.MaxCheerNoteLength);
    }
}
