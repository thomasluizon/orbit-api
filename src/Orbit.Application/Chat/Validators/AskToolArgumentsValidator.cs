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
        RuleFor(value => value).Custom((value, context) =>
        {
            try
            {
                IssuedAstraBlock.Create(Guid.NewGuid(), ToDefinition(value));
            }
            catch (ArgumentException error)
            {
                context.AddFailure(new FluentValidation.Results.ValidationFailure("definition", error.Message)
                {
                    ErrorCode = ErrorCodes.AstraBlockInvalidDefinition
                });
            }
        });
    }

    internal static AstraBlockDefinition ToDefinition(AskToolArguments value) => new(
        value.Form, (value.Form == AstraBlockKinds.Choice ? value.Question
            : value.Title ?? (value.Form == AstraBlockKinds.Question ? value.Question : null)) ?? string.Empty,
        value.Options ?? [], value.Items ?? [], value.Form == AstraBlockKinds.PickList || value.MultiSelect,
        value.Form != AstraBlockKinds.Question && value.AllowFreeText, value.Action, value.Questions,
        value.Form == AstraBlockKinds.Question ? value.Intro : null, value.Progress);
}
