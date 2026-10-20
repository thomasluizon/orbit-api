using System.Globalization;
using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class UpdateGoalTool(
    IGenericRepository<Goal> goalRepository,
    IUnitOfWork unitOfWork) : IAiTool, IConcurrencyRetryableTool, IArgumentCheckTool
{
    public string Name => "update_goal";

    public string Description =>
        "Update goal fields. Null keeps values; clear_fields removes descriptions or deadlines.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            clear_fields = OptionalToolArguments.ClearFieldsSchema(OptionalToolArguments.GoalClearFields),
            goal_id = new { type = JsonSchemaTypes.String },
            title = new { type = JsonSchemaTypes.String, nullable = true },
            description = new { type = JsonSchemaTypes.String, description = "Description; clear via clear_fields.", nullable = true },
            target_value = new { type = "number" },
            unit = new { type = JsonSchemaTypes.String },
            deadline = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD deadline; clear via clear_fields.", nullable = true }
        },
        required = new[] { "goal_id" }
    };

    public async Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (resolvedArgs, argumentError) = OptionalToolArguments.ResolveClears(args, OptionalToolArguments.GoalClearFields);
        if (argumentError is not null)
            return Orbit.Domain.Common.Result.Failure(argumentError);
        args = resolvedArgs;

        if (!GoalToolHelpers.TryParseGoalId(args, out var goalId))
            return Orbit.Domain.Common.Result.Failure("Invalid goal_id.");
        var goals = await goalRepository.FindAsync(g => g.Id == goalId && g.UserId == userId, ct);
        var goal = goals.FirstOrDefault();
        if (goal is null)
            return Orbit.Domain.Common.Result.Failure("Goal not found.");
        var command = new Orbit.Application.Goals.Commands.UpdateGoalCommand(userId, goalId,
            ResolveTitle(args, goal), ResolveDescription(args, goal), ResolveTargetValue(args, goal),
            ResolveUnit(args, goal), ResolveDeadline(args, goal));
        var validation = new Orbit.Application.Goals.Validators.UpdateGoalCommandValidator().Validate(command);
        if (!validation.IsValid)
            return Orbit.Domain.Common.Result.Failure(validation.ToString());
        return Goal.Create(new Goal.CreateGoalParams(userId, command.Title, command.TargetValue,
            command.Unit, command.Description, command.Deadline));
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (resolvedArgs, argumentError) = OptionalToolArguments.ResolveClears(args, OptionalToolArguments.GoalClearFields);
        if (argumentError is not null)
            return new ToolResult(false, Error: argumentError);
        args = resolvedArgs;

        if (!GoalToolHelpers.TryParseGoalId(args, out var goalId))
            return GoalToolHelpers.InvalidGoalIdResult();

        var goal = await GoalToolHelpers.FindGoalAsync(goalRepository, goalId, userId, ct);
        if (goal is null)
            return GoalToolHelpers.GoalNotFoundResult(goalId);

        var title = ResolveTitle(args, goal);
        var description = ResolveDescription(args, goal);
        var targetValue = ResolveTargetValue(args, goal);
        var unit = ResolveUnit(args, goal);
        var deadline = ResolveDeadline(args, goal);

        var result = goal.Update(title, description, targetValue, unit, deadline);
        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        await unitOfWork.SaveChangesAsync(ct);
        return new ToolResult(true, EntityId: goal.Id.ToString(), EntityName: goal.Title);
    }

    private static string ResolveTitle(JsonElement args, Goal goal) =>
        JsonArgumentParser.PropertyExists(args, "title")
            ? JsonArgumentParser.GetNullableString(args, "title") ?? goal.Title
            : goal.Title;

    private static string? ResolveDescription(JsonElement args, Goal goal) =>
        JsonArgumentParser.PropertyExists(args, "description")
            ? JsonArgumentParser.GetNullableString(args, "description")
            : goal.Description;

    private static decimal ResolveTargetValue(JsonElement args, Goal goal)
    {
        if (!args.TryGetProperty("target_value", out var targetValueEl)
            || targetValueEl.ValueKind != JsonValueKind.Number)
        {
            return goal.TargetValue;
        }

        return targetValueEl.GetDecimal();
    }

    private static string ResolveUnit(JsonElement args, Goal goal) =>
        JsonArgumentParser.PropertyExists(args, "unit")
            ? JsonArgumentParser.GetNullableString(args, "unit") ?? goal.Unit
            : goal.Unit;

    private static DateOnly? ResolveDeadline(JsonElement args, Goal goal)
    {
        if (!JsonArgumentParser.PropertyExists(args, "deadline"))
            return goal.Deadline;

        var deadlineString = JsonArgumentParser.GetNullableString(args, "deadline");
        if (deadlineString is null)
            return null;

        return DateOnly.TryParseExact(
            deadlineString,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var deadline)
            ? deadline
            : goal.Deadline;
    }
}
