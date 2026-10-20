using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Domain.Enums;

namespace Orbit.Api.Serialization;

/// <summary>Adds clock reminder input aliases while preserving MVC's enum converter behavior.</summary>
internal sealed class ScheduledReminderWhenInputConverter : JsonConverter<ScheduledReminderWhen>
{
    private static readonly JsonConverter<ScheduledReminderWhen> Baseline =
        (JsonConverter<ScheduledReminderWhen>)new JsonStringEnumConverter()
            .CreateConverter(typeof(ScheduledReminderWhen), new JsonSerializerOptions());

    public override ScheduledReminderWhen Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            switch (reader.GetString())
            {
                case "same_day": return ScheduledReminderWhen.SameDay;
                case "day_before": return ScheduledReminderWhen.DayBefore;
            }
        }

        return Baseline.Read(ref reader, typeToConvert, options);
    }

    public override void Write(Utf8JsonWriter writer, ScheduledReminderWhen value, JsonSerializerOptions options) =>
        Baseline.Write(writer, value, options);
}
