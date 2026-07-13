using FluentValidation;
using Orbit.Application.ChecklistTemplates.Queries;

namespace Orbit.Application.ChecklistTemplates.Validators;

public class GetChecklistTemplatesQueryValidator : AbstractValidator<GetChecklistTemplatesQuery>
{
    public GetChecklistTemplatesQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
