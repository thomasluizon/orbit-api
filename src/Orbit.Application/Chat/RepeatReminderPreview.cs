using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class RepeatReminderPreview
{
    public static string? Format(string? value, AgentClientContext? client, TimeZoneInfo zone)
    {
        if (string.IsNullOrEmpty(value) || value == "null")
            return null;
        using var document = JsonDocument.Parse("{\"repeat_reminder\":" + value + "}");
        var repeat = RepeatReminderToolArguments.Parse(document.RootElement)!;
        var pt = LocaleHelper.IsPortuguese(client?.Locale);
        var hours = repeat.IntervalMinutes / 60;
        var interval = repeat.IntervalMinutes < 60
            ? pt ? $"A cada {repeat.IntervalMinutes} minutos" : $"Every {repeat.IntervalMinutes} minutes"
            : pt ? $"A cada {hours} {(hours == 1 ? "hora" : "horas")}" : $"Every {hours} {(hours == 1 ? "hour" : "hours")}";
        var culture = CultureInfo.GetCultureInfo(pt ? "pt-BR" : "en-US");
        var format = TimeFormatResolver.Uses24HourClock(zone.Id) ? "HH:mm" : "h:mm tt";
        if (repeat.ActiveStart is { } start && repeat.ActiveEnd is { } end)
            interval += pt ? $"; das {start.ToString(format, culture)} às {end.ToString(format, culture)}"
                : $"; active {start.ToString(format, culture)} to {end.ToString(format, culture)}";
        if (repeat.StartAtUtc is { } instant)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(instant, zone).ToString((pt ? "dd/MM/yyyy " : "M/d/yyyy ") + format, culture);
            interval += pt ? $"; a partir de {local}" : $"; starting {local}";
        }
        return interval;
    }
}
