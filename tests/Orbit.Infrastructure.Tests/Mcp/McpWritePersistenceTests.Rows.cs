using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Mcp;

public partial class McpWritePersistenceTests
{
    private static object Arguments(string method, Guid habit, Guid parent, Guid goal, Guid tag, Guid template, Guid? skip)
    {
        var filter = System.Text.Json.JsonSerializer.Serialize(new { habit_ids = new[] { habit } });
        return method switch
        {
            "CreateHabit" => new { title = "Created", dueDate = "2026-04-03", isGeneral = true },
            "UpdateHabit" => new { habitId = habit, title = "Changed" },
            "DeleteHabit" or "LogHabit" or "UnlogHabit" or "SkipHabit" or "DuplicateHabit" => new { habitId = habit },
            "UndoSkipHabit" => new { habitId = habit, skipId = skip },
            "UpdateChecklist" => new { habitId = habit, checklistItemsJson = "[{\"text\":\"Water\",\"isChecked\":true}]" },
            "CreateSubHabit" => new { parentHabitId = parent, title = "Child", frequencyUnit = "Day", frequencyQuantity = 1 },
            "BulkCreateHabits" => new { habitsJson = "[{\"title\":\"Created\",\"isGeneral\":true}]" },
            "BulkDeleteHabits" or "BulkLogHabits" or "BulkSkipHabits" => new { habitIds = habit.ToString() },
            "BulkUpdateHabits" => new { filterJson = filter, updatesJson = "{\"description\":\"Changed\"}" },
            "BulkUpdateHabitEmojis" => new { filterJson = filter, emoji = "📚", replaceExisting = true },
            "BulkRescheduleHabits" => new { filterJson = filter, dueDate = "2026-04-05" },
            "ReorderHabits" => new { positionsJson = $"[{{\"habitId\":\"{habit}\",\"position\":3}}]" },
            "MoveHabitParent" => new { habitId = habit, parentId = parent },
            "LinkGoalsToHabit" => new { habitId = habit, goalIds = goal.ToString() },
            "CreateGoal" => new { title = "Created", targetValue = 20, unit = "books" },
            "UpdateGoal" => new { goalId = goal, title = "Changed", targetValue = 20, unit = "books" },
            "DeleteGoal" => new { goalId = goal },
            "UpdateGoalProgress" => new { goalId = goal, currentValue = 3 },
            "UpdateGoalStatus" => new { goalId = goal, status = "Completed" },
            "ReorderGoals" => new { positionsJson = $"[{{\"id\":\"{goal}\",\"position\":3}}]" },
            "LinkHabitsToGoal" => new { goalId = goal, habitIds = habit.ToString() },
            "CreateTag" => new { name = "Created", color = "#654321" },
            "UpdateTag" => new { tagId = tag, name = "Changed", color = "#654321" },
            "DeleteTag" => new { tagId = tag },
            "AssignTags" => new { habitId = habit, tagIds = tag.ToString() },
            "CreateChecklistTemplate" => new { name = "Created", items = "Water,Read" },
            "UpdateChecklistTemplate" => new { templateId = template, name = "Changed", items = new[] { "Read", "Walk" } },
            "DeleteChecklistTemplate" => new { templateId = template },
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown wrapper")
        };
    }

    private static async Task AssertPersisted(OrbitDbContext db, string method, Guid habitId, Guid parentId, Guid goalId, Guid tagId, Guid templateId)
    {
        var habit = await db.Habits.IgnoreQueryFilters().Include(item => item.Tags).Include(item => item.Goals).SingleAsync(item => item.Id == habitId);
        var goal = await db.Goals.IgnoreQueryFilters().Include(item => item.Habits).SingleAsync(item => item.Id == goalId);
        switch (method)
        {
            case "CreateHabit":
            case "BulkCreateHabits": (await db.Habits.SingleAsync(item => item.Title == "Created")).Schedule.Should().Be(HabitSchedule.General); break;
            case "CreateSubHabit": (await db.Habits.SingleAsync(item => item.Title == "Child")).ParentHabitId.Should().Be(parentId); break;
            case "DuplicateHabit": (await db.Habits.CountAsync()).Should().Be(3); break;
            case "UpdateHabit": habit.Title.Should().Be("Changed"); break;
            case "DeleteHabit":
            case "BulkDeleteHabits": habit.IsDeleted.Should().BeTrue(); break;
            case "LogHabit":
            case "BulkLogHabits": (await db.HabitLogs.SingleAsync(item => item.HabitId == habitId)).Date.Should().Be(Today); break;
            case "UnlogHabit": (await db.HabitLogs.AnyAsync(item => item.HabitId == habitId)).Should().BeFalse(); break;
            case "SkipHabit":
            case "BulkSkipHabits": habit.NextDueDate.Should().BeAfter(Today); break;
            case "UndoSkipHabit": habit.NextDueDate.Should().Be(Today); (await db.Set<HabitSkipUndo>().SingleAsync()).IsUndone.Should().BeTrue(); break;
            case "UpdateChecklist": habit.ChecklistItems.Should().ContainSingle().Which.Text.Should().Be("Water"); break;
            case "BulkUpdateHabits": habit.Description.Should().Be("Changed"); break;
            case "BulkUpdateHabitEmojis": habit.Emoji.Should().Be("📚"); break;
            case "BulkRescheduleHabits": habit.NextDueDate.Should().Be(Today.AddDays(2)); break;
            case "ReorderHabits": habit.Position.Should().Be(0); break;
            case "MoveHabitParent": habit.ParentHabitId.Should().Be(parentId); break;
            case "LinkGoalsToHabit": habit.Goals.Should().ContainSingle().Which.Id.Should().Be(goalId); break;
            default: await AssertOtherEntities(db, method, habit, goal, tagId, templateId); break;
        }
    }

    private static async Task AssertOtherEntities(OrbitDbContext db, string method, Habit habit, Goal goal, Guid tagId, Guid templateId)
    {
        switch (method)
        {
            case "CreateGoal": (await db.Goals.SingleAsync(item => item.Title == "Created")).TargetValue.Should().Be(20); break;
            case "UpdateGoal": goal.Title.Should().Be("Changed"); goal.TargetValue.Should().Be(20); break;
            case "DeleteGoal": goal.IsDeleted.Should().BeTrue(); break;
            case "UpdateGoalProgress": goal.CurrentValue.Should().Be(3); (await db.Set<GoalProgressLog>().CountAsync()).Should().Be(1); break;
            case "UpdateGoalStatus": goal.Status.Should().Be(GoalStatus.Completed); break;
            case "ReorderGoals": goal.Position.Should().Be(0); break;
            case "LinkHabitsToGoal": goal.Habits.Should().ContainSingle().Which.Id.Should().Be(habit.Id); break;
            case "CreateTag": (await db.Tags.SingleAsync(item => item.Name == "Created")).Color.Should().Be("#654321"); break;
            case "UpdateTag": var tag = await db.Tags.SingleAsync(item => item.Id == tagId); tag.Name.Should().Be("Changed"); tag.Color.Should().Be("#654321"); break;
            case "DeleteTag": (await db.Tags.AnyAsync(item => item.Id == tagId)).Should().BeFalse(); break;
            case "AssignTags": habit.Tags.Should().ContainSingle().Which.Id.Should().Be(tagId); break;
            case "CreateChecklistTemplate": (await db.ChecklistTemplates.SingleAsync(item => item.Name == "Created")).Items.Should().Equal("Water", "Read"); break;
            case "UpdateChecklistTemplate": var template = await db.ChecklistTemplates.SingleAsync(item => item.Id == templateId); template.Name.Should().Be("Changed"); template.Items.Should().Equal("Read", "Walk"); break;
            case "DeleteChecklistTemplate": (await db.ChecklistTemplates.AnyAsync(item => item.Id == templateId)).Should().BeFalse(); break;
            default: throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown wrapper assertion");
        }
    }
}
