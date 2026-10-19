using System.Text.Json.Serialization;

namespace Orbit.Application.Chat;

public sealed record ToolResultMessageParams(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntityName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TotalCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SkippedCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Partial = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WeekStartDay = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Hours = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BulkTool = null);
