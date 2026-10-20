using FluentValidation;
using Orbit.Application.ChecklistTemplates.Commands;

namespace Orbit.Application.ChecklistTemplates.Validators;

public class CreateChecklistTemplateCommandValidator : AbstractValidator<CreateChecklistTemplateCommand>
{
    public CreateChecklistTemplateCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Name).AddTemplateNameRules();
        this.AddTemplateItemsRules(x => x.Items);
    }
}
