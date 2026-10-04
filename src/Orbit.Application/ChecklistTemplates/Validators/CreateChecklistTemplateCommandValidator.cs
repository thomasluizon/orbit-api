using FluentValidation;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.ChecklistTemplates.Validators;

public class CreateChecklistTemplateCommandValidator : AbstractValidator<CreateChecklistTemplateCommand>
{
    public CreateChecklistTemplateCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Items)
            .Must(items => items.Count <= AppConstants.MaxChecklistItems)
            .When(x => x.Items is not null)
            .WithCopy(ValidationErrorCodes.TemplateItemLimit);

        RuleForEach(x => x.Items)
            .NotEmpty()
            .WithCopy(ValidationErrorCodes.TemplateItemRequired)
            .MaximumLength(AppConstants.MaxChecklistItemTextLength)
            .WithCopy(ValidationErrorCodes.TemplateItemLength);
    }
}
