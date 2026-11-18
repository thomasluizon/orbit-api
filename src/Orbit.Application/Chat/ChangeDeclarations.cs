using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

/// <summary>
/// The declaration shape of the typed change list on the <c>ai_change_plan</c> path, from the setting
/// <c>AI:ChangePlanShape</c>. For the compiler, the shape is a declaration change only.
/// </summary>
public enum ChangeDeclarationShape
{
    /// <summary>One tool for each change kind. The calls of one response form its change list.</summary>
    Kinds,

    /// <summary>One <c>propose_changes</c> tool that holds the change list.</summary>
    Wrapper
}

/// <summary>
/// The model-facing declarations of the typed change list and the reader of the change list in a model response.
/// </summary>
public static class ChangeDeclarations
{
    public const string WrapperName = "propose_changes";
    public const string PlanTitleArgument = "plan_title";
    public const string ChangesArgument = "changes";

    private const string PlanTitleDescription = "A title of at most 40 characters for the plan of this request, in the person's language, when the request holds two or more changes. Null for one change.";

    private const string WrapperDescription = "Propose every account change of the request as one list. The server prepares the list as one unit: one approval preview for the changes that can share it, one preview for each other change, and nothing when a change has an error. It saves nothing before the person's approval.";

    private const string SharedInstruction = "The server prepares every change of one response as one unit and saves nothing before the person's approval. "
        + "When a change has an error, the server prepares nothing and returns the error list: each error names the change id, the argument path, a code, a message and, where the server knows them, the allowed values or the candidate records. Correct every listed error and send the whole change list again once. "
        + "A change that names a record of an ask in the same response waits for the answer and returns waits_for_answer; send it again after the answer. "
        + "One response holds at most 3 previews: all changes that can share one preview count as one. "
        + "Each change kind covers one record or many: use one change with habit_ids or a filter for the same action on several habits. A skip uses bulk_skip_habits, never a log. "
        + "When a create change returns missing_field, ask for that field with draft_call_id set to the change id and missing_field set to the error path. "
        + "If a change returns HABIT_LIMIT_REACHED, explain the returned limit in the person's language. It applies to every plan; do not propose upgrading, deleting data or bypassing it. "
        + "Corrections to an earlier preview still use revise_pending_changes and preserve all unrelated fields and items.";

    /// <summary>The prompt instruction of the shape, in place of <c>present_plan</c> and the write directive of the old path.</summary>
    public static string Instruction(ChangeDeclarationShape shape) => shape == ChangeDeclarationShape.Wrapper
        ? "Send every account change of the request in one propose_changes call: one item in changes for each change, with a short id that is unique in the list, its kind and the arguments of that kind. "
            + $"Set {PlanTitleArgument} when the list holds two or more changes. Set {TerminalWriteDirective.CompleteArgument} to true when the list covers the entire request, and false when a read or a dependent change is still needed. "
            + SharedInstruction
        : "Send every account change of the request in one response: one call of its kind for each change. "
            + $"When the response holds two or more changes, set {PlanTitleArgument} on one of them. Set {TerminalWriteDirective.CompleteArgument} to true on every change of a response that covers the entire request, and false when a read or a dependent change is still needed. "
            + SharedInstruction;

    /// <summary>The <c>plan_title</c> argument that each declaration of a change adds.</summary>
    public static JsonObject PlanTitleSchema() => new()
    {
        ["type"] = "string",
        ["description"] = PlanTitleDescription
    };

    /// <summary>Adds <c>plan_title</c> to the schema of one change kind of the kinds shape.</summary>
    public static object WithPlanTitle(object schema)
    {
        var root = JsonSerializer.SerializeToNode(schema)!.AsObject();
        root["properties"] ??= new JsonObject();
        root["properties"]!.AsObject()[PlanTitleArgument] = PlanTitleSchema();
        ChatToolControlOrder.Apply(root);
        return root;
    }

    /// <summary>
    /// The <c>propose_changes</c> declaration: a list of changes, each an object with its id, its kind and the arguments
    /// of that kind, one alternative for each kind. <paramref name="kinds"/> holds each kind with its description and
    /// the schema of its arguments. <paramref name="controls"/> adds the completion and offer controls once, at the top.
    /// </summary>
    public static object Wrapper(IEnumerable<(string Kind, string Description, object Arguments)> kinds,
        Func<object, object> controls) => new
        {
            name = WrapperName,
            description = WrapperDescription,
            parameters = controls(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    [ChangesArgument] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["description"] = "Every change of the request.",
                        ["items"] = new JsonObject
                        {
                            ["anyOf"] = new JsonArray([.. kinds.Select(kind => (JsonNode)new JsonObject
                            {
                                ["type"] = "object",
                                ["description"] = kind.Description,
                                ["properties"] = new JsonObject
                                {
                                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "A short id of this change, unique in the list." },
                                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(kind.Kind) },
                                    ["arguments"] = JsonSerializer.SerializeToNode(kind.Arguments)
                                },
                                ["required"] = new JsonArray("id", "kind", "arguments")
                            })])
                        }
                    },
                    [PlanTitleArgument] = PlanTitleSchema()
                },
                ["required"] = new JsonArray(ChangesArgument)
            })
        };

    /// <summary>True when <paramref name="call"/> is a change call of <paramref name="shape"/>.</summary>
    public static bool IsChangeCall(AiToolCall call, ChangeDeclarationShape shape, AiToolRegistry registry) =>
        shape == ChangeDeclarationShape.Wrapper
            ? call.Name == WrapperName
            : registry.GetTool(call.Name) is { } tool && ChangeKinds.IsKind(tool);

    /// <summary>
    /// The changes of one change call. A wrapper call holds the list; a change without a readable id gets the id of its
    /// call and its place. A kind call is one change with the id of the call.
    /// </summary>
    public static IReadOnlyList<ProposedChange> Read(AiToolCall call, ChangeDeclarationShape shape)
    {
        if (shape == ChangeDeclarationShape.Kinds)
            return [new ProposedChange(call.Id, call.Name, Without(call.Args, PlanTitleArgument))];
        if (call.Args.ValueKind != JsonValueKind.Object || !call.Args.TryGetProperty(ChangesArgument, out var changes)
            || changes.ValueKind != JsonValueKind.Array)
            return [];
        return [.. changes.EnumerateArray().Select((change, index) => new ProposedChange(
            change.ValueKind == JsonValueKind.Object && change.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())
                ? id.GetString()! : $"{call.Id}/{index}",
            change.ValueKind == JsonValueKind.Object && change.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String ? kind.GetString()! : string.Empty,
            change.ValueKind == JsonValueKind.Object && change.TryGetProperty("arguments", out var arguments)
                ? arguments.Clone() : default))];
    }

    /// <summary>The first plan title of the change calls of one response.</summary>
    public static string? PlanTitle(IEnumerable<AiToolCall> calls) => calls
        .Select(call => call.Args.ValueKind == JsonValueKind.Object && call.Args.TryGetProperty(PlanTitleArgument, out var title)
            && title.ValueKind == JsonValueKind.String ? title.GetString()?.Trim() : null)
        .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));

    private static JsonElement Without(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(property, out _)) return args;
        var node = JsonNode.Parse(args.GetRawText())!.AsObject();
        node.Remove(property);
        return JsonSerializer.SerializeToElement(node);
    }
}
