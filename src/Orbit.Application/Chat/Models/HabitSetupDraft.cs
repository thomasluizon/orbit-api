namespace Orbit.Application.Chat.Models;

public sealed record HabitSetupDraft(string CallId, string Title, string MissingField, Guid ClarificationId);

public sealed record HabitSetupMissingFieldResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("draft_call_id")] string DraftCallId,
    [property: System.Text.Json.Serialization.JsonPropertyName("missing_field")] string MissingField,
    string Title,
    NeedsClarificationPayload Clarification);
