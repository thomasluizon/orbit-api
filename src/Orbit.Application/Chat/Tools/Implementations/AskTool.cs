using System.Text.Json;
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
    public string Name => "ask";
    public string Description => "Ask for missing input with a choice, a pick list for a bulk preview, or a multi-field question form. Read account data first. Ends the reply. Never request approval or repeat a preview question. "
        + "A request to choose habits requires ask, even with one candidate. Use pickList for selecting habits to log or skip, with action and items; it prepares no write. "
        + "For a single choice among existing records, use form choice with top-level question and options, even when form question is supported. Reserve form question for multiple fields. If the change is also missing, ask for both target and change in the choice question with allowFreeText=true. Use only a form the client supports.";
    public bool IsReadOnly => true;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public object GetParameterSchema()
    {
        var createSchema = JsonSerializer.SerializeToElement(createTool.GetParameterSchema());
        return new
        {
            type = "object",
            properties = new
            {
                form = new { type = "string", @enum = new[] { "choice", "pickList", "question" }, description = "choice requires a nonempty question and 2 to 4 options; omit items, action, questions and progress. pickList requires nonempty title, action and 1 to 10 items; omit options, questions and progress, and set allowFreeText=false. question requires 1 to 10 questions; omit top-level options, items and action and set multiSelect=false. A single record selection uses choice; reserve question for multiple fields. Use only a supported form." },
                question = new { type = "string", maxLength = 120, description = "Question, at most 120 characters." },
                title = new { type = "string", description = "Required nonempty heading for pickList; optional heading of at most 120 characters for question." },
                multiSelect = new { type = "boolean", description = "For choice only. pickList always selects multiple items; question requires false or null." },
                allowFreeText = new { type = "boolean", description = "For choice only; false for pickList. In form question, each questions entry controls its own allowFreeText; this top-level value is ignored." },
                intro = new { type = "string", maxLength = 500, description = "Optional nonempty introduction of at most 500 characters for question only. Omit for choice and pickList." },
                progress = new
                {
                    type = "object",
                    description = "For question only. Requires 0 <= current <= total and total >= 1.",
                    properties = new { current = new { type = "integer", minimum = 0 }, total = new { type = "integer", minimum = 1 } },
                    required = new[] { "current", "total" }
                },
                questions = new
                {
                    type = "array",
                    description = "Required for question only. Use unique nonempty question ids. singleChoice and multipleChoice require 2 to 10 options even with allowFreeText=true. scale requires lowLabel and highLabel. slider requires min, max, step and unit. yesNo has no options. Omit fields belonging to another question type.",
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
                                description = "Required for singleChoice and multipleChoice. Omit or null for scale, slider and yesNo. Option ids must be unique within the question.",
                                minItems = 2,
                                maxItems = 10,
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        id = new { type = "string", maxLength = 80, description = "Nonempty option id, at most 80 characters." },
                                        label = new { type = "string", maxLength = 120, description = "Nonempty label, at most 120 characters." },
                                        recommended = new { type = "boolean", description = "At most one recommended option, only for singleChoice." }
                                    },
                                    required = new[] { "id", "label" }
                                }
                            },
                            allowFreeText = new { type = "boolean", description = "Allow a custom answer for singleChoice, multipleChoice, or yesNo. Must be false or null for scale and slider." },
                            lowLabel = new { type = "string", maxLength = 120, description = "Required nonempty label for 1 on a scale, at most 120 characters. Omit for other types." },
                            highLabel = new { type = "string", maxLength = 120, description = "Required nonempty label for 5 on a scale, at most 120 characters. Omit for other types." },
                            min = new { type = "number", minimum = -1000000000, maximum = 1000000000, description = "Required slider lower bound. Omit for other types." },
                            max = new { type = "number", minimum = -1000000000, maximum = 1000000000, description = "Required slider upper bound, greater than min. Omit for other types." },
                            step = new { type = "number", description = "Required positive slider step dividing max minus min exactly. Omit for other types." },
                            unit = new { type = "string", maxLength = 24, description = "Required nonempty slider unit in the person's language, at most 24 characters. Omit for other types." }
                        },
                        required = new[] { "id", "type", "prompt" }
                    }
                },
                options = new
                {
                    type = "array",
                    minItems = 2,
                    maxItems = 4,
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new { type = "string", description = "Nonempty unique option id." },
                            label = new { type = "string", maxLength = 20, description = "Nonempty option label, at most 20 characters." },
                            description = new { type = "string", maxLength = 24, description = "Supporting text, at most 24 characters." }
                        },
                        required = new[] { "id", "label" }
                    }
                },
                action = new { type = "string", @enum = new[] { "create_habits", "log_habits", "skip_habits" } },
                items = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = 10,
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new { type = "string", description = "Nonempty unique item id." },
                            label = new { type = "string", description = "Nonempty item label." },
                            detail = new { type = "string", maxLength = 24, description = "Supporting text, at most 24 characters." },
                            arguments = new
                            {
                                description = "One bulk item: a habit definition for create_habits, or {habit_id, date} for log_habits/skip_habits. Dates use YYYY-MM-DD.",
                                anyOf = new object[]
                                {
                                    createSchema.GetProperty("properties").GetProperty("habits").GetProperty("items"),
                                    new { type = "object", properties = new { habit_id = new { type = "string" }, date = new { type = "string" } }, required = new[] { "habit_id", "date" } }
                                }
                            }
                        },
                        required = new[] { "id", "label", "arguments" }
                    }
                }
            },
            required = new[] { "form" }
        };
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        AskToolArguments? input;
        try
        {
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
