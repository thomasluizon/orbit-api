using System.Globalization;
using System.Text.RegularExpressions;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

/// <summary>
/// Decides on the server whether a create preview is a dated event that can use Google Calendar.
/// Only a one-time draft with a requested date qualifies. A due date equal to the person's today is a
/// default create date unless the message names a date.
/// </summary>
internal static partial class CalendarOfferEligibility
{
    public static bool HasRequestedEventDate(PendingAgentOperation preview, string? message, DateOnly today) =>
        preview.Items is { } items && items.Any(item => IsRequestedEventDate(item.Fields, message, today));

    private static bool IsRequestedEventDate(IReadOnlyList<PendingOperationChange> fields, string? message, DateOnly today)
    {
        if (fields.Any(field => field.Field == "is_general" && field.NewValue == "true")) return false;
        var unit = fields.FirstOrDefault(field => field.Field == "frequency_unit")?.NewValue;
        if (unit is not (null or "" or "OneTime")) return false;
        var date = fields.FirstOrDefault(field => field.Field == "due_date" && field.ProposedValue is not null && field.Provenance == "requested");
        if (date is null || !DateOnly.TryParseExact(date.NewValue, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
            return false;
        return due != today || NamesDate(message);
    }

    internal static bool NamesDate(string? message) => message is not null && DateMention().IsMatch(message);

    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}(?:/\d{2,4})?|today|tonight|tomorrow|hoje|amanh[ãa]|esta\s+noite|"
        + @"monday|tuesday|wednesday|thursday|friday|saturday|sunday|"
        + @"segunda|ter[çc]a|quarta|quinta|sexta|s[áa]bado|domingo|"
        + @"january|february|march|april|may|june|july|august|september|october|november|december|"
        + @"janeiro|fevereiro|mar[çc]o|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro|"
        + @"dia\s+\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateMention();
}
