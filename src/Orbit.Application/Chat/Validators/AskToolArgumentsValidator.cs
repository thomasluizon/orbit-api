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
                IssuedAiBlock.Create(Guid.NewGuid(), ToDefinition(value));
            }
            catch (ArgumentException error)
            {
                context.AddFailure(new FluentValidation.Results.ValidationFailure("definition", error.Message)
                {
                    ErrorCode = ErrorCodes.AiBlockInvalidDefinition
                });
            }
        });
    }

    internal static AiBlockDefinition ToDefinition(AskToolArguments value) => new(
        value.Form, (value.Form == AiBlockKinds.Choice ? value.Question
            : value.Title ?? (value.Form == AiBlockKinds.Question ? value.Question : null)) ?? string.Empty,
        value.Options ?? [], value.Items ?? [], value.Form == AiBlockKinds.PickList || value.MultiSelect,
        value.Form != AiBlockKinds.Question && value.AllowFreeText, value.Action, value.Questions,
        value.Form == AiBlockKinds.Question ? value.Intro : null, value.Progress);
}
