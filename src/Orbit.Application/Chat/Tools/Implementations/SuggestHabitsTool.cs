using System.Text.Json;
using MediatR;
using Orbit.Application.Chat.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class SuggestHabitsTool(IMediator mediator) : IAiTool
{
    public string Name => "suggest_habits";
    public bool IsReadOnly => true;
    public string Description => "Suggest personal, editable habit drafts from current habits, goals and free schedule time. "
        + "Use when the person asks for habit ideas or editable habit drafts. Help starting an activity uses suggest_breakdown with one_small_step=true instead. "
        + "Return habitSuggestions instead of a plain list. Pass the person's request and its language. "
        + "This never creates habits. The person edits a draft and requests its approval preview before saving. "
        + "For an unstructured message that also requests habit ideas, keep its explicit tasks and put the chosen draft fields "
        + "in one bulk_create_habits approval preview; map existingHabitId to habit_id to reuse the habit. Wait for these drafts before preparing that preview, copy their fields unchanged and keep stored titles verbatim. Never invent a quantity, rewrite a draft title or replace its activity. The combined preview replaces the separate suggestion block.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            instruction = new { type = JsonSchemaTypes.String, maxLength = 1000 },
            language = new { type = JsonSchemaTypes.String, @enum = new[] { "en", "pt", "pt-BR" } },
            count = new { type = JsonSchemaTypes.Integer, minimum = 1, maximum = HabitSuggestionsCard.MaxDrafts }
        },
        required = new[] { "instruction", "language" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var instruction = JsonArgumentParser.GetOptionalString(args, "instruction") ?? string.Empty;
        var language = JsonArgumentParser.GetOptionalString(args, "language") ?? "en";
        var count = JsonArgumentParser.GetOptionalInt(args, "count") ?? 3;
        var result = await mediator.Send(new SuggestHabitDraftsQuery(userId, instruction, language, count), ct);
        return result.IsSuccess ? new(true, Payload: result.Value) : ToolResult.FromFailure(result);
    }
}
