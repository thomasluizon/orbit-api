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
    public string Description => "Ask one short choice question, or offer suggested items to select together for a bulk write preview. Write each choice option label as a short phrase of two to four words, at most 20 characters, and each option description or pick list item detail as one short line, at most 24 characters. Ends the reply. Never use for approval or a yes/no question already asked by a preview.";
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
                form = new { type = "string", @enum = new[] { "choice", "pickList" } },
                question = new { type = "string", maxLength = 120 },
                title = new { type = "string" },
                multiSelect = new { type = "boolean" },
                allowFreeText = new { type = "boolean" },
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
                            label = new { type = "string", maxLength = 20 },
                            description = new { type = "string", maxLength = 24 }
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
                            detail = new { type = "string", maxLength = 24 },
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
        args = OptionalToolArguments.OmitNulls(args);
        AskToolArguments? input;
        try
        {
            input = args.Deserialize<AskToolArguments>(JsonOptions);
        }
        catch (JsonException)
        {
            return new ToolResult(false, Error: "Invalid ask arguments.");
        }
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
