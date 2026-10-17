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
            .WithCopy(ErrorMessages.ClarificationValueEmpty.Code)
            .MaximumLength(AppConstants.MaxClarificationValueLength)
            .WithCopy(ErrorMessages.ClarificationValueTooLong.Code, AppConstants.MaxClarificationValueLength)
            .Must(BeJsonObject)
            .WithCopy(ErrorMessages.ClarificationValueNotJsonObject.Code);
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
