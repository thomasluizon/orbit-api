using FluentValidation;
using Orbit.Application.Proactive.Commands;
using Orbit.Application.Proactive.Queries;

namespace Orbit.Application.Proactive.Validators;

public sealed class GetCurrentProactiveQueryValidator : AbstractValidator<GetCurrentProactiveQuery>
{
    public GetCurrentProactiveQueryValidator() => RuleFor(x => x.UserId).NotEmpty();
}

public sealed class RecordProactiveOutcomeCommandValidator : AbstractValidator<RecordProactiveOutcomeCommand>
{
    public RecordProactiveOutcomeCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.ItemId).NotEmpty();
    }
}

public sealed class GetProactiveItemStatusQueryValidator : AbstractValidator<GetProactiveItemStatusQuery>
{
    public GetProactiveItemStatusQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.ItemId).NotEmpty();
    }
}
