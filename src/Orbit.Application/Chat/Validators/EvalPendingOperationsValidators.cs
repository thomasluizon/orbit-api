using FluentValidation;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;

namespace Orbit.Application.Chat.Validators;

public sealed class ClearEvalPendingOperationsCommandValidator : AbstractValidator<ClearEvalPendingOperationsCommand>
{
    public ClearEvalPendingOperationsCommandValidator() => RuleFor(request => request.UserId).NotEmpty();
}

public sealed class GetEvalPendingOperationsQueryValidator : AbstractValidator<GetEvalPendingOperationsQuery>
{
    public GetEvalPendingOperationsQueryValidator() => RuleFor(request => request.UserId).NotEmpty();
}
