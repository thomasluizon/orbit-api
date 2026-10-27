using System.Text.RegularExpressions;

namespace Orbit.Application.Chat;

internal static partial class PendingPreviewRouting
{
    public static bool OffersPriorPreviews(string message) =>
        Date().Matches(message).Select(match => match.Value).Distinct().Take(2).Count() < 2
        || Revision().IsMatch(message);

    public static bool OffersTool(string tool, string message) =>
        tool != "revise_pending_changes" || OffersPriorPreviews(message);

    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}(?:/\d{2,4})?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Date();

    [GeneratedRegex(@"\b(?:change|move|reschedule|correct|update|rename|remove|delete|revise|preview|mude|muda|altere|alterar|corrija|corrige|atualize|renomeie|remova|exclua|troque|reagende|pr[eé]via)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Revision();
}
