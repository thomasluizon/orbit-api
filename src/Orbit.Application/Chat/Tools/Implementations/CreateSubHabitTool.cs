using System.Text.Json;
using System.Text.Json.Nodes;
using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public class CreateSubHabitTool(IMediator mediator) : IAiTool, IArgumentCheckTool, ITransactionalAiTool
{
    public bool SupportsAtomicExecution(JsonElement arguments) =>
        arguments.TryGetProperty(ToolArgumentNames.ParentHabitId, out _) && !BulkCreateHabitsTool.HasGoalLinks(arguments);

    public string Name => "create_sub_habit";
    public int Order => 1;
    public string Description => "Create a sub-habit under an existing parent with the complete habit fields, checklist, tags, goals and nested children. " + HabitModelInstructions.All;
    public object GetParameterSchema()
    {
        var schema = JsonSerializer.SerializeToNode(BulkCreateHabitsTool.HabitItemSchema(AppConstants.MaxHabitDepth))!.AsObject();
        schema["required"] = new JsonArray("parent_habit_id", "title");
        return schema;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!HasParent(args)) return new(false, Error: "parent_habit_id is required and must be a valid GUID.");
        if (args.TryGetProperty("frequency_unit", out var frequency) && frequency.ValueKind == JsonValueKind.Null)
        {
            var node = JsonNode.Parse(args.GetRawText())!.AsObject();
            node["frequency_unit"] = "OneTime";
            args = JsonSerializer.SerializeToElement(node);
        }
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        return await new BulkCreateHabitsTool(mediator).ExecuteSingleAsync(args, userId, ct);
    }

    public Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        !HasParent(args) ? Task.FromResult(Result.Failure("parent_habit_id is required and must be a valid GUID."))
            : new BulkCreateHabitsTool(mediator).CheckArgumentsAsync(JsonSerializer.SerializeToElement(new { habits = new[] { args } }), userId, ct);

    private static bool HasParent(JsonElement args) => args.ValueKind == JsonValueKind.Object
        && Guid.TryParse(JsonArgumentParser.GetOptionalString(args, "parent_habit_id"), out _);
}
