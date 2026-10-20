using System.Linq.Expressions;
using FluentValidation;
using Orbit.Application.Common;

namespace Orbit.Application.ChecklistTemplates.Validators;

internal static class SharedChecklistTemplateRules
{
    public static void AddTemplateNameRules<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithFieldCopy(ValidationCopyKeys.TemplateNameRequired)
            .MaximumLength(100).WithFieldCopy(ValidationCopyKeys.TemplateNameLength);

    public static void AddTemplateItemsRules<T>(this AbstractValidator<T> validator,
        Expression<Func<T, IReadOnlyList<string>?>> items)
    {
        validator.RuleFor(items)
            .Must(value => value is null || value.Count <= AppConstants.MaxChecklistItems)
            .WithCopy(ValidationErrorCodes.TemplateItemLimit);
        validator.RuleForEach(Expression.Lambda<Func<T, IEnumerable<string>>>(items.Body, items.Parameters))
            .NotEmpty().WithCopy(ValidationErrorCodes.TemplateItemRequired)
            .MaximumLength(AppConstants.MaxChecklistItemTextLength)
            .WithCopy(ValidationErrorCodes.TemplateItemLength);
    }
}
