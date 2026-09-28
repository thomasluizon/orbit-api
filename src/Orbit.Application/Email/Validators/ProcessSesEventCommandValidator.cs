using FluentValidation;
using Orbit.Application.Email.Commands;

namespace Orbit.Application.Email.Validators;

public sealed class ProcessSesEventCommandValidator : AbstractValidator<ProcessSesEventCommand>
{
    public ProcessSesEventCommandValidator()
    {
        RuleFor(command => command.Payload).NotEmpty().MaximumLength(262144);
    }
}
