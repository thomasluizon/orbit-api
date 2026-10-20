using FluentValidation;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.ChecklistTemplates.Validators;

public class UpdateChecklistTemplateCommandValidator : AbstractValidator<UpdateChecklistTemplateCommand>
{
    public UpdateChecklistTemplateCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x).Must(x => x.Name is not null || x.Items is not null)
            .WithMessage("Provide a template name, items, or both.");
        When(x => x.Name is not null, () => RuleFor(x => x.Name).AddTemplateNameRules());
        When(x => x.Items is not null, () =>
        {
            RuleFor(x => x.Items).NotEmpty().WithCopy(ValidationErrorCodes.TemplateItemRequired);
            this.AddTemplateItemsRules(x => x.Items);
        });
    }
}
