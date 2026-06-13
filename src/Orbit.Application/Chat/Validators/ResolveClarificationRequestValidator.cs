using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Validators;

public class ResolveClarificationRequestValidator : AbstractValidator<ResolveClarificationRequest>
{
    public ResolveClarificationRequestValidator()
    {
        RuleFor(x => x.Value)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage(ErrorMessages.ClarificationValueEmpty.Message)
            .WithErrorCode(ErrorMessages.ClarificationValueEmpty.Code)
            .MaximumLength(AppConstants.MaxClarificationValueLength)
            .WithMessage(ErrorMessages.ClarificationValueTooLong.Format(AppConstants.MaxClarificationValueLength).Message)
            .WithErrorCode(ErrorMessages.ClarificationValueTooLong.Code)
            .Must(BeJsonObject)
            .WithMessage(ErrorMessages.ClarificationValueNotJsonObject.Message)
            .WithErrorCode(ErrorMessages.ClarificationValueNotJsonObject.Code);
    }

    private static bool BeJsonObject(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            return JsonNode.Parse(value) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
