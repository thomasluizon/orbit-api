using System.Text.Json.Serialization;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed record HabitMonthCard(DateOnly DateFrom, DateOnly DateTo, DateOnly Today, int WeekStartDay,
    CalendarMonthResponse Calendar, string Language = "en", string SurfaceId = "calendar",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReadBlockMetadata? Read = null);

public static class HabitMonthCardBuilder
{
    public const string Kind = "habitMonth";

    public static string Introduction(HabitMonthCard card) => LocaleHelper.IsPortuguese(card.Language)
        ? "Aqui está seu calendário de hábitos." : "Here is your habit calendar.";
}
