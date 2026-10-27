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
    public string Description => "Ask for missing input with a question block, a choice, or a pick list for a bulk preview. Read account data first. Ends the reply. Never request approval or repeat a preview question.";
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
                form = new { type = "string", @enum = new[] { "choice", "pickList", "question" } },
                question = new { type = "string", maxLength = 120, description = "Question, at most 120 characters." },
                title = new { type = "string" },
                multiSelect = new { type = "boolean" },
                allowFreeText = new { type = "boolean" },
                intro = new { type = "string", maxLength = 500 },
                progress = new
                {
                    type = "object",
                    properties = new { current = new { type = "integer", minimum = 1 }, total = new { type = "integer", minimum = 1 } },
                    required = new[] { "current", "total" }
                },
                questions = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = 10,
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new { type = "string", maxLength = 80 },
                            type = new { type = "string", @enum = new[] { "singleChoice", "multipleChoice", "scale", "slider", "yesNo" } },
                            prompt = new { type = "string", maxLength = 500 },
                            options = new
                            {
                                type = "array",
                                minItems = 2,
                                maxItems = 10,
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        id = new { type = "string", maxLength = 80 },
                                        label = new { type = "string", maxLength = 120 },
                                        recommended = new { type = "boolean", description = "At most one recommended option, only for singleChoice." }
                                    },
                                    required = new[] { "id", "label" }
                                }
                            },
                            allowFreeText = new { type = "boolean", description = "Allow a custom answer for singleChoice, multipleChoice, or yesNo." },
                            lowLabel = new { type = "string", maxLength = 120, description = "Required label for 1 on a scale." },
                            highLabel = new { type = "string", maxLength = 120, description = "Required label for 5 on a scale." },
                            min = new { type = "number", minimum = -1000000000, maximum = 1000000000 },
                            max = new { type = "number", minimum = -1000000000, maximum = 1000000000 },
                            step = new { type = "number", description = "Positive slider step dividing max minus min exactly." },
                            unit = new { type = "string", maxLength = 24, description = "Required slider unit in the person's language." }
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
                            id = new { type = "string" },
                            label = new { type = "string", maxLength = 20, description = "Option label, at most 20 characters." },
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
                            id = new { type = "string" },
                            label = new { type = "string" },
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
        if (input is null || !(await new AskToolArgumentsValidator().ValidateAsync(input, ct)).IsValid)
            return new ToolResult(false, Error: "Invalid ask form, question, options, items, or action.");
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
