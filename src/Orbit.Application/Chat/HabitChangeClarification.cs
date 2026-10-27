using System.Text.RegularExpressions;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static partial class HabitChangeClarification
{
    internal static bool IsUnspecified(string message) => UnspecifiedChange().IsMatch(message.Trim());

    internal static AiResponse CompleteText(AiResponse response, string message, string? language) =>
        IsUnspecified(message) && response.ToolCalls?.Any(call => call.Name == "ask") != true
            ? response with { TextMessage = Question(language), ToolCalls = null }
            : response;

    internal static string Question(string? language) => LocaleHelper.IsPortuguese(language)
        ? "Qual hábito você quer mudar e o que quer mudar nele?"
        : "Which habit would you like to change, and what would you like to change?";

    [GeneratedRegex(@"^(?:please\s+)?(?:change|edit|update|modify)\s+(?:my|the|a)\s+habit(?:\s+please)?[.!?]*$|^(?:por favor[,]?\s+)?(?:altere|altera|mude|muda|edite|editar|alterar|mudar)\s+(?:o\s+)?(?:meu|um|o)\s+h[aá]bito(?:[,]?\s+por favor)?[.!?]*$", RegexOptions.IgnoreCase)]
    private static partial Regex UnspecifiedChange();
}
