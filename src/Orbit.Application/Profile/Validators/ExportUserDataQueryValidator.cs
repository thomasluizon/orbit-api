using FluentValidation;
using Orbit.Application.Profile.Queries;

namespace Orbit.Application.Profile.Validators;

public class ExportUserDataQueryValidator : AbstractValidator<ExportUserDataQuery>
{
    public ExportUserDataQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
