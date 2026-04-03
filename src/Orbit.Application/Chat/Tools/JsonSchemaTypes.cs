namespace Orbit.Application.Chat.Tools;

/// <summary>
/// Shared JSON Schema type constants used across AI tool parameter schemas.
/// Eliminates S1192 string literal duplication in tool definitions.
/// </summary>
internal static class JsonSchemaTypes
{
    internal const string String = "string";
    internal const string Object = "object";
    internal const string Array = "array";
    internal const string Boolean = "boolean";
    internal const string Integer = "integer";

    internal static readonly string[] FrequencyUnitEnum = ["Day", "Week", "Month", "Year"];
    internal static readonly string[] ScheduledReminderWhenEnum = ["day_before", "same_day"];
}
