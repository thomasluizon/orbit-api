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
        // Stop on first failure so an empty string doesn't also trip the JSON-object
        // check — the controller surfaces only Errors[0] anyway, and the redundant
        // failure is noise.
        RuleFor(x => x.Value)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage(ErrorMessages.ClarificationValueEmpty)
            .MaximumLength(AppConstants.MaxClarificationValueLength)
            .WithMessage(string.Format(ErrorMessages.ClarificationValueTooLong, AppConstants.MaxClarificationValueLength))
            .Must(BeJsonObject)
            .WithMessage(ErrorMessages.ClarificationValueNotJsonObject);
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
