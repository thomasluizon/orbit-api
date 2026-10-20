using FluentValidation;
using Orbit.Application.Chat.Commands;

namespace Orbit.Application.Chat.Validators;

public sealed class ExecuteAgentMutationCommandValidator : AbstractValidator<ExecuteAgentMutationCommand>
{
    public ExecuteAgentMutationCommandValidator()
    {
        RuleFor(command => command.Request.UserId).NotEmpty();
        RuleFor(command => command.Request.OperationId).NotEmpty();
    }
}
