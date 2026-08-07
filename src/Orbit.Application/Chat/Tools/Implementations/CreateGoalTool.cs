using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class CreateGoalTool : IAiTool
{
    private readonly IGenericRepository<Goal> _goalRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IGenericRepository<Habit>? _habitRepository;

    [ActivatorUtilitiesConstructor]
    public CreateGoalTool(
        IGenericRepository<Goal> goalRepository,
        IUnitOfWork unitOfWork,
        IGenericRepository<Habit> habitRepository)
    {
        _goalRepository = goalRepository;
        _unitOfWork = unitOfWork;
        _habitRepository = habitRepository;
    }

    public CreateGoalTool(
        IGenericRepository<Goal> goalRepository,
        IUnitOfWork unitOfWork)
    {
        _goalRepository = goalRepository;
        _unitOfWork = unitOfWork;
    }

    public string Name => "create_goal";
    public string Description => "Create a new goal to track progress toward a target. Pass habit_ids inline in this call rather than following up with link_habits_to_goal. Goals can have a target value and unit (e.g., 'read 12 books', 'lose 5 kg'). If the user doesn't specify a target, use target_value=1 and unit='goal'. Use when user wants to track measurable long-term progress. Use goal_type='Streak' to create a streak goal that tracks the habit's consecutive day streak.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            title = new { type = JsonSchemaTypes.String, description = "Name of the goal" },
            description = new { type = JsonSchemaTypes.String, description = "Optional description" },
            target_value = new { type = "number", description = "Target number to reach (default: 1)" },
            unit = new { type = JsonSchemaTypes.String, description = "Unit of measurement (e.g., 'books', 'kg', 'dollars', 'goal')" },
            deadline = new { type = JsonSchemaTypes.String, description = "Optional deadline in YYYY-MM-DD format" },
            goal_type = new { type = JsonSchemaTypes.String, description = "Goal type: 'Standard' (default) or 'Streak' (tracks consecutive habit streak)" },
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                description = "Optional IDs of habits to link to the new goal",
                items = new { type = JsonSchemaTypes.String }
            }
        },
        required = new[] { "title" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("title", out var titleEl) || string.IsNullOrWhiteSpace(titleEl.GetString()))
            return new ToolResult(false, Error: "title is required.");

        var targetValue = args.TryGetProperty("target_value", out var targetEl) && targetEl.ValueKind == JsonValueKind.Number
            ? targetEl.GetDecimal() : 1m;
        var unit = args.TryGetProperty("unit", out var unitEl) && !string.IsNullOrWhiteSpace(unitEl.GetString())
            ? unitEl.GetString()! : "goal";

        string? description = args.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String ? descEl.GetString() : null;
        DateOnly? deadline = null;
        if (args.TryGetProperty("deadline", out var dlEl) && dlEl.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(dlEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            deadline = parsed;

        var goalType = GoalType.Standard;
        if (args.TryGetProperty("goal_type", out var goalTypeEl) && goalTypeEl.ValueKind == JsonValueKind.String)
            Enum.TryParse(goalTypeEl.GetString(), ignoreCase: true, out goalType);

        var habitIdsFailure = TryParseHabitIds(args, out var habitIds);
        if (habitIdsFailure is not null)
            return habitIdsFailure;

        if (habitIds.Count > AppConstants.MaxHabitsPerGoal)
            return new ToolResult(false, Error: ErrorMessages.MaxHabitsPerGoal.Format(AppConstants.MaxHabitsPerGoal).Message);

        var goalResult = Goal.Create(new Goal.CreateGoalParams(
            userId,
            titleEl.GetString() ?? string.Empty,
            targetValue,
            unit,
            description,
            deadline,
            Type: goalType));
        if (goalResult.IsFailure) return ToolResult.FromFailure(goalResult);

        var goal = goalResult.Value;
        if (habitIds.Count > 0)
        {
            if (_habitRepository is null)
                return new ToolResult(false, Error: "habit_ids is unavailable for this tool instance.");

            var habits = await _habitRepository.FindTrackedAsync(
                h => habitIds.Contains(h.Id) && h.UserId == userId,
                ct);

            var habitsResolved = OwnershipValidation.AllResolved(habitIds, habits, h => h.Id, ErrorMessages.HabitNotFound);
            if (habitsResolved.IsFailure)
                return ToolResult.FromFailure(habitsResolved);

            foreach (var habit in habits)
                goal.AddHabit(habit);
        }

        await _goalRepository.AddAsync(goal, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return new ToolResult(true, EntityId: goal.Id.ToString(), EntityName: goal.Title);
    }

    private static ToolResult? TryParseHabitIds(JsonElement args, out List<Guid> habitIds)
    {
        habitIds = [];
        if (!args.TryGetProperty("habit_ids", out var habitIdsElement))
            return null;

        if (habitIdsElement.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "habit_ids must be an array.");

        foreach (var item in habitIdsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var habitId))
                return new ToolResult(false, Error: "habit_ids must contain only valid GUID strings.");

            habitIds.Add(habitId);
        }

        return null;
    }
}
