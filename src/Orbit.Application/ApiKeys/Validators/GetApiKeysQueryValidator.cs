using FluentValidation;
using Orbit.Application.ApiKeys.Queries;

namespace Orbit.Application.ApiKeys.Validators;

public class GetApiKeysQueryValidator : AbstractValidator<GetApiKeysQuery>
{
    public GetApiKeysQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
