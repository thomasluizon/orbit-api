using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class AskTool(
    BulkCreateHabitsTool createTool,
    BulkLogHabitsTool logTool,
    BulkSkipHabitsTool skipTool,
    IUserDateService userDateService) : IChatOnlyAiTool
{
    public const string TargetResolutionInstruction = "A habit identified by id or by an exact unique title is resolved. Different actions on different identified habits are not ambiguous. Ask only for input that is still missing after reading account data. In a compound request, call the independent write tools before ask so their approval preview appears beside the question; withhold only changes that depend on the answer. Never ask the person to choose again between already identified targets. A single pick uses choice even when it has a heading; use question for two or more fields.";
    public const string SingleSelectionInstruction = "A single discrete selection always uses ask form choice with top-level question and options, including when the question also requests a free-text change. Reserve form question for multiple separate input fields.";
    public const string DuplicateTargetsInstruction = "Duplicate targets: A singular name matching duplicate titles remains unresolved even when the account index contains every matching ID. Call query_habits with search and for_selection=true, then ask with form choice; never pass all matching IDs to bulk_log_habits or any other bulk write.";
    public const string AccountReadInstruction = "Before a successful typed account read in this turn, ask is unavailable unless a create tool returned a typed missing-field result for a new habit draft. Submit the requested draft to its create tool first, preserving unspecified fields as null; do not invent missing values to avoid that result. Then ask only for the returned missing_field of its draft_call_id. Never create a draft for advice, a reminder, an existing record, a preference or a check-in. Read account records to resolve those requests; when the read answers the request, return it with orbit_read_complete=true and do not ask.";

    public string Name => "ask";
    public string Description => "Ask for missing input with a choice, a pick list for a bulk preview, or a multi-field question form. Read account data first. Only a valid, accepted ask ends the reply. Never request approval or repeat a preview question. "
        + "Do not call ask for a clear request you can act on, including a batch of dated items or a request to show habits and goals. Null fields are absent; form question requires nonempty questions. "
        + "An explicit request for the person to choose habits requires ask, even with one candidate. Use pickList for selecting habits to log or skip, with action and items; it prepares no write. "
        + "For a single choice among existing records, use form choice with top-level question and options, even when form question is supported. Reserve form question for multiple fields. If the change is also missing, ask for both target and change in the choice question with allowFreeText=true. Use only a form the client supports. "
        + "Put the selected form and its fields inside input. "
        + SingleSelectionInstruction + " " + DuplicateTargetsInstruction + " "
        + TargetResolutionInstruction + " " + RestartPlanInstructions.ProposalBoundary + " " + AccountReadInstruction;
    public bool IsReadOnly => true;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public object GetParameterSchema()
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                input = new
                {
                    type = "object",
                    properties = new
                    {
                        missing_input = new
                        {
                            type = "string",
                            @enum = new[] { "habit_setup", "preference", "check_in", "record_selection", "bulk_selection" },
                            description = "Identify input only the person can supply: habit_setup, preference, check_in, record_selection for one existing record, or bulk_selection for several existing records. Record and bulk selection require query_habits with for_selection=true earlier in this turn. Resolve an earlier Astra message from history; never ask which message it was."
                        },
                        form = new { type = "string", @enum = new[] { "choice", "pickList", "question" }, description = "choice requires a nonempty question and 2 to 4 options; omit items, action, questions and progress. pickList requires nonempty title, action and 1 to 10 items; omit options, questions and progress, and set allowFreeText=false. question requires 1 to 10 questions; omit top-level options, items and action and set multiSelect=false. A single record selection uses choice; reserve question for multiple fields. Use only a supported form." },
                        question = new { type = "string", maxLength = 120, description = "Required nonempty question for choice, at most 120 characters. Use null for other forms." },
                        title = new { type = "string", description = "Required nonempty heading for pickList; optional heading of at most 120 characters for question. Null means absent." },
                        multiSelect = new { type = "boolean", description = "For choice only; null means false. pickList always selects multiple items; question requires false or null." },
                        allowFreeText = new { type = "boolean", description = "For choice only; null means false. Must be false for pickList. In form question, each questions entry controls its own allowFreeText; this top-level value is ignored." },
                        intro = new { type = "string", maxLength = 500, description = "Optional nonempty introduction of at most 500 characters for question only. Use null for choice and pickList." },
                        progress = new
                        {
                            type = "object",
                            description = "For question only. Requires 0 <= current <= total and total >= 1. Null means absent.",
                            properties = new { current = new { type = "integer", minimum = 0 }, total = new { type = "integer", minimum = 1 } },
                            required = new[] { "current", "total" }
                        },
                        questions = new
                        {
                            type = "array",
                            description = "Required for question only. Use unique nonempty question ids. singleChoice and multipleChoice require 2 to 10 options even with allowFreeText=true. scale requires lowLabel and highLabel. slider requires min, max, step and unit. yesNo has no options. Null means absent and is allowed only for other forms. Use null for fields belonging to another question type.",
                            minItems = 1,
                            maxItems = 10,
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    id = new { type = "string", maxLength = 80, description = "Nonempty unique question id, at most 80 characters." },
                                    type = new { type = "string", @enum = new[] { "singleChoice", "multipleChoice", "scale", "slider", "yesNo" } },
                                    prompt = new { type = "string", maxLength = 500, description = "Nonempty prompt, at most 500 characters." },
                                    options = new
                                    {
                                        type = "array",
                                        description = "Required for singleChoice and multipleChoice. Null means absent for scale, slider and yesNo. Option ids must be unique within the question.",
                                        minItems = 2,
                                        maxItems = 10,
                                        items = new
                                        {
                                            type = "object",
                                            properties = new
                                            {
                                                id = new { type = "string", maxLength = 80, description = "Nonempty option id, at most 80 characters." },
                                                label = new { type = "string", maxLength = 120, description = "Nonempty label, at most 120 characters." },
                                                recommended = new { type = "boolean", description = "At most one recommended option, only for singleChoice. Null means false." }
                                            },
                                            required = new[] { "id", "label" }
                                        }
                                    },
                                    allowFreeText = new { type = "boolean", description = "Allow a custom answer for singleChoice, multipleChoice, or yesNo. Null means false; must be false or null for scale and slider." },
                                    lowLabel = new { type = "string", maxLength = 120, description = "Required nonempty label for 1 on a scale, at most 120 characters. Use null for other types." },
                                    highLabel = new { type = "string", maxLength = 120, description = "Required nonempty label for 5 on a scale, at most 120 characters. Use null for other types." },
                                    min = new { type = "number", minimum = -1000000000, maximum = 1000000000, description = "Required slider lower bound. Use null for other types." },
                                    max = new { type = "number", minimum = -1000000000, maximum = 1000000000, description = "Required slider upper bound, greater than min. Use null for other types." },
                                    step = new { type = "number", description = "Required positive slider step dividing max minus min exactly. Use null for other types." },
                                    unit = new { type = "string", maxLength = 24, description = "Required nonempty slider unit in the person's language, at most 24 characters. Use null for other types." }
                                },
                                required = new[] { "id", "type", "prompt" }
                            }
                        },
                        options = new
                        {
                            type = "array",
                            description = "Required for choice. Null means absent for other forms.",
                            minItems = 2,
                            maxItems = 4,
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    id = new { type = "string", description = "Nonempty unique option id." },
                                    label = new { type = "string", maxLength = 20, description = "Nonempty option label, at most 20 characters." },
                                    description = new { type = "string", maxLength = 24, description = "Supporting text, at most 24 characters. Null means absent." }
                                },
                                required = new[] { "id", "label" }
                            }
                        },
                        action = new { type = "string", @enum = new[] { "create_habits", "log_habits", "skip_habits" }, description = "Required bulk action for pickList. Null means absent for other forms." },
                        items = new
                        {
                            type = "array",
                            description = "Required for pickList. Null means absent for other forms.",
                            minItems = 1,
                            maxItems = 10,
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    id = new { type = "string", description = "Nonempty unique item id." },
                                    label = new { type = "string", description = "Nonempty item label." },
                                    detail = new { type = "string", maxLength = 24, description = "Supporting text, at most 24 characters. Null means absent." },
                                    arguments = new
                                    {
                                        description = "For create_habits, a JSON string containing only the supplied fields of one bulk_create_habits item, including title; omit unspecified fields and nulls. For log_habits/skip_habits, an object with only habit_id and date (YYYY-MM-DD). Never repeat the full create schema or pad arguments with whitespace.",
                                        anyOf = new object[]
                                        {
                                            new { type = "string" },
                                            new { type = "object", properties = new { habit_id = new { type = "string" }, date = new { type = "string" } }, required = new[] { "habit_id", "date" } }
                                        }
                                    }
                                },
                                required = new[] { "id", "label", "arguments" }
                            }
                        }
                    },
                    required = new[] { "form", "missing_input" }
                }
            },
            required = new[] { "input" }
        };
        return BuildFormSchema(JsonSerializer.SerializeToNode(schema)!.AsObject());
    }

    public object GetCacheableParameterSchema()
    {
        var schema = (JsonObject)GetParameterSchema();
        foreach (var form in schema["properties"]!["input"]!["anyOf"]!.AsArray())
        {
            var fields = form!["properties"]!.AsObject();
            fields["draft_call_id"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Copy the typed missing-field result's draft_call_id when asking about a new habit draft; otherwise null."
            };
            fields["missing_field"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Copy that draft's missing_field exactly and ask only for that field; otherwise null."
            };
        }
        return schema;
    }

    public object GetParameterSchema(bool hasAccountRead, IReadOnlyList<HabitSetupDraft>? drafts = null)
    {
        var schema = (JsonObject)GetParameterSchema();
        if (hasAccountRead) return schema;
        if (drafts is not { Count: > 0 })
            throw new InvalidOperationException("ask requires a typed account read or a create result with missing fields.");
        var forms = schema["properties"]!["input"]!["anyOf"]!.AsArray();
        var boundForms = new JsonArray();
        foreach (var draft in drafts)
            foreach (var source in forms)
            {
                if (source!["properties"]!["form"]!["enum"]![0]!.GetValue<string>() == AstraBlockKinds.PickList) continue;
                var form = source!.DeepClone();
                var fields = form["properties"]!.AsObject();
                var reason = fields["missing_input"]!;
                reason["enum"] = new JsonArray("habit_setup");
                reason["description"] = AccountReadInstruction;
                fields["draft_call_id"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(draft.CallId) };
                fields["missing_field"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(draft.MissingField) };
                if (fields["questions"] is { } questions)
                {
                    questions["maxItems"] = 1;
                    foreach (var question in questions["items"]!["anyOf"]!.AsArray())
                        question!["properties"]!["id"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(draft.MissingField) };
                }
                form["required"]!.AsArray().Add("draft_call_id");
                form["required"]!.AsArray().Add("missing_field");
                boundForms.Add(form);
            }
        schema["properties"]!["input"]!["anyOf"] = boundForms;
        return schema;
    }

    private static JsonObject BuildFormSchema(JsonObject schema)
    {
        var fields = schema["properties"]!["input"]!["properties"]!.AsObject();
        var questionFields = fields["questions"]!["items"]!["properties"]!.AsObject();
        fields["questions"]!["items"] = new JsonObject
        {
            ["anyOf"] = new JsonArray(
                Variant(questionFields, "type", "singleChoice", ["id", "prompt", "options"], ["allowFreeText"]),
                Variant(questionFields, "type", "multipleChoice", ["id", "prompt", "options"], ["allowFreeText"]),
                Variant(questionFields, "type", "scale", ["id", "prompt", "lowLabel", "highLabel"], []),
                Variant(questionFields, "type", "slider", ["id", "prompt", "min", "max", "step", "unit"], []),
                Variant(questionFields, "type", "yesNo", ["id", "prompt"], ["allowFreeText"]))
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["input"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                        Variant(fields, "form", "choice", ["missing_input", "question", "options"], ["multiSelect", "allowFreeText"]),
                        Variant(fields, "form", "pickList", ["missing_input", "title", "action", "items"], []),
                        Variant(fields, "form", "question", ["missing_input", "questions"], ["title", "intro", "progress"]))
                }
            },
            ["required"] = new JsonArray("input")
        };
    }

    private static JsonObject Variant(JsonObject fields, string discriminator, string value,
        string[] required, string[] optional)
    {
        var marker = fields[discriminator]!.DeepClone();
        marker["enum"] = new JsonArray(value);
        var properties = new JsonObject
        {
            [discriminator] = marker
        };
        foreach (var name in required.Concat(optional))
        {
            var field = fields[name]!.DeepClone();
            if (required.Contains(name) && field["type"]?.GetValue<string>() == "string"
                && field["enum"] is null)
                field["pattern"] = @"\S";
            properties[name] = field;
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(new[] { discriminator }.Concat(required)
                .Select(name => (JsonNode?)JsonValue.Create(name)).ToArray())
        };
    }

    internal static JsonElement ExecutionArguments(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("input", out var input) ? input : args;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        args = OptionalToolArguments.OmitNulls(ExecutionArguments(args));
        AskToolArguments? input;
        try
        {
            args = ExpandPickListArguments(args);
            input = args.Deserialize<AskToolArguments>(JsonOptions);
        }
        catch (JsonException)
        {
            return new ToolResult(false, Error: "Invalid ask arguments.");
        }
        if (input is not null && (input.Question?.Length > 120
            || input.Options?.Any(option => option is not null && (option.Label?.Length > 20 || option.Description?.Length > 24)) == true
            || input.Items?.Any(item => item is not null && item.Detail?.Length > 24) == true))
            return new ToolResult(false, Error: "ask text is too long: question max 120, option label max 20, option description and item detail max 24 characters. Shorten the text and retry.");
        if (input is null)
            return new ToolResult(false, Error: "Invalid ask arguments.");
        var validation = await new AskToolArgumentsValidator().ValidateAsync(input, ct);
        if (!validation.IsValid)
            return new ToolResult(false, Error: string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        var definition = AskToolArgumentsValidator.ToDefinition(input);
        if (definition.Form == AstraBlockKinds.PickList)
        {
            if (definition.Action == "create_habits")
            {
                var today = await userDateService.GetUserTodayAsync(userId, ct);
                definition = definition with
                {
                    Items = definition.Items.Select(item => item.Arguments.TryGetProperty("due_date", out _)
                        ? item : item with { Arguments = FreezeCreateDate(item.Arguments, today) }).ToArray()
                };
            }
            var check = GetCheck(definition.Action!);
            foreach (var item in definition.Items)
            {
                var result = await check.CheckArgumentsAsync(BuildBulkArguments(definition.Action!, [item]), userId, ct);
                if (result.IsFailure)
                    return ToolResult.FromFailure(result);
            }
            var combined = await check.CheckArgumentsAsync(BuildBulkArguments(definition.Action!, definition.Items), userId, ct);
            if (combined.IsFailure)
                return ToolResult.FromFailure(combined);
        }
        return new ToolResult(true, Payload: definition);
    }

    private static JsonElement ExpandPickListArguments(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array) return args;
        var expanded = JsonNode.Parse(args.GetRawText())!;
        foreach (var item in expanded["items"]!.AsArray())
            if (item is JsonObject fields && fields["arguments"] is JsonValue value
                && value.TryGetValue<string>(out var json))
            {
                var arguments = JsonNode.Parse(json);
                if (arguments is not JsonObject) throw new JsonException("Pick list arguments must be an object.");
                fields["arguments"] = arguments;
            }
        return OptionalToolArguments.OmitNulls(JsonSerializer.SerializeToElement(expanded));
    }

    private IArgumentCheckTool GetCheck(string action) => action switch
    {
        "create_habits" => createTool,
        "log_habits" => logTool,
        "skip_habits" => skipTool,
        _ => throw new ArgumentException("Unsupported pick list action.", nameof(action))
    };

    private static JsonElement FreezeCreateDate(JsonElement arguments, DateOnly date)
    {
        var fields = arguments.EnumerateObject().ToDictionary(field => field.Name, field => field.Value);
        fields["due_date"] = JsonSerializer.SerializeToElement(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        return JsonSerializer.SerializeToElement(fields);
    }

    public static JsonElement BuildBulkArguments(string action, IReadOnlyList<AstraBlockItem> items) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            [action == "create_habits" ? "habits" : "revised_items"] = items.Select(item => item.Arguments).ToArray()
        });
}
