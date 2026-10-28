using FluentValidation;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Validators;

public sealed class AskToolArgumentsValidator : AbstractValidator<AskToolArguments>
{
    public AskToolArgumentsValidator()
    {
        RuleFor(value => value).Must(HasValidDefinition).WithCopy(ErrorCodes.AstraBlockInvalidDefinition);
    }

    private static bool HasValidDefinition(AskToolArguments value)
    {
        try
        {
            IssuedAstraBlock.Create(Guid.NewGuid(), ToDefinition(value));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static AstraBlockDefinition ToDefinition(AskToolArguments value) => new(
        value.Form, (value.Form == AstraBlockKinds.Choice ? value.Question : value.Title) ?? string.Empty,
        value.Options ?? [], value.Items ?? [], value.Form == AstraBlockKinds.PickList || value.MultiSelect,
        value.AllowFreeText, value.Action, value.Questions,
        value.Form == AstraBlockKinds.Question ? value.Intro : null, value.Progress);
}
