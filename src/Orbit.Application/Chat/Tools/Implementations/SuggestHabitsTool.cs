using System.Text.Json;
using MediatR;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class SuggestHabitsTool(IMediator mediator) : IAiTool
{
    public string Name => "suggest_habits";
    public bool IsReadOnly => true;
    public string Description => "Suggest personal, editable habit drafts from current habits, goals and free schedule time. "
        + "Use when the person asks for habit ideas or editable habit drafts. Help starting an activity uses suggest_breakdown with one_small_step=true instead. "
        + "Return habitSuggestions instead of a plain list. Pass the person's request and its language. "
        + "Keep the person's requested topics in instruction; never put those topics in excluded_activities for ordinary suggestions. "
        + "This never creates habits. The person edits a draft and requests its approval preview before saving. "
        + "For additional ideas in a combined preview, supply excluded_activities with every activity already planned, using stored titles for reused habits. An empty list still requests new activities only. Omit it for ordinary suggestions that may adjust existing habits. "
        + "For an unstructured message that also requests habit ideas, keep its explicit tasks and put the chosen draft fields "
        + "in one bulk_create_habits approval preview; map existingHabitId to habit_id to reuse the habit. Wait for these drafts before preparing that preview, copy their fields unchanged and keep stored titles verbatim. Never invent a quantity, rewrite a draft title or replace its activity. The combined preview replaces the separate suggestion block. "
        + HabitModelInstructions.BrainDumpCompletion;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            instruction = new { type = JsonSchemaTypes.String, maxLength = 1000 },
            language = new { type = JsonSchemaTypes.String, @enum = new[] { "en", "pt", "pt-BR" } },
            count = new { type = JsonSchemaTypes.Integer, minimum = 1, maximum = HabitSuggestionsCard.MaxDrafts },
            excluded_activities = new
            {
                type = JsonSchemaTypes.Array,
                description = "Activities already planned for the combined preview. Return new activities outside this list and the account's existing habits, including synonyms; never offer those activities as adjustments. For ordinary suggestions, omit this field or use null; requested topics are not exclusions.",
                maxItems = HabitSuggestionRequest.MaxExcludedActivities,
                items = new { type = JsonSchemaTypes.String, minLength = 1, maxLength = HabitSuggestionRequest.MaxExcludedActivityLength }
            }
        },
        required = new[] { "instruction", "language" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var instruction = JsonArgumentParser.GetOptionalString(args, "instruction") ?? string.Empty;
        var language = JsonArgumentParser.GetOptionalString(args, "language") ?? "en";
        var count = JsonArgumentParser.GetOptionalInt(args, "count") ?? 3;
        IReadOnlyList<string>? excluded = null;
        if (args.TryGetProperty("excluded_activities", out var values) && values.ValueKind != JsonValueKind.Null)
        {
            if (values.ValueKind != JsonValueKind.Array || values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                return InvalidExclusions(language);
            excluded = values.EnumerateArray().Select(value => value.GetString()!).ToArray();
            if (!HabitSuggestionRequest.HasValidExclusions(excluded)) return InvalidExclusions(language);
        }
        var result = await mediator.Send(new SuggestHabitDraftsQuery(userId, instruction, language, count, excluded), ct);
        return result.IsSuccess ? new(true, Payload: result.Value) : ToolResult.FromFailure(result);
    }

    private static ToolResult InvalidExclusions(string language) => new(false,
        Error: ErrorCopy.Resolve("HABIT_SUGGESTION_INVALID", LocaleHelper.IsPortuguese(language)), ErrorCode: "HABIT_SUGGESTION_INVALID");
}
