using FluentValidation;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Validators;

public sealed class AskToolArgumentsValidator : AbstractValidator<AskToolArguments>
{
    public AskToolArgumentsValidator()
    {
        RuleFor(value => value).Custom((value, context) =>
        {
            try
            {
                IssuedAstraBlock.Create(Guid.NewGuid(), ToDefinition(value));
            }
            catch (ArgumentException)
            {
                context.AddFailure("Invalid ask form, question, options, items, or action.");
            }
        });
    }

    internal static AstraBlockDefinition ToDefinition(AskToolArguments value) => new(
        value.Form, (value.Form == AstraBlockKinds.Choice ? value.Question : value.Title) ?? string.Empty,
        value.Options ?? [], value.Items ?? [], value.Form == AstraBlockKinds.PickList || value.MultiSelect,
        value.AllowFreeText, value.Action);
}
