using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    /// <summary>
    /// The records that an answered choice stands for enter the turn as read and as resolved. A write to them then
    /// passes the unread-target check and the selection check, because the person already decided them.
    /// </summary>
    private static void SeedAnsweredChoice(AiChoiceSelection? choice, IReadOnlyList<Habit> habits, Guid userId,
        ToolExecutionAccumulator results)
    {
        if (choice is null)
            return;
        var targets = choice.Targets.Select(target => Guid.TryParse(target, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToHashSet();
        var parentTitles = habits.ToDictionary(habit => habit.Id, habit => habit.Title);
        foreach (var habit in habits.Where(habit => habit.UserId == userId && targets.Contains(habit.Id)))
        {
            results.AnsweredTargetIds.Add(habit.Id);
            results.ReadHabitItems[habit.Id.ToString()] = new HabitListCardItem(habit.Id.ToString(), habit.Title,
                habit.Emoji, 0, habit.IsBadHabit, HabitListCardBuilder.StatusNone, habit.StartDate, habit.ParentHabitId,
                habit.ParentHabitId is { } parentId ? parentTitles.GetValueOrDefault(parentId) : null);
        }
    }

    /// <summary>
    /// The ids of the records that a choice option names are records that the turn read. An id that no read
    /// returned is a guess, so the block is refused.
    /// </summary>
    private static string? AskTargetError(AiBlockDefinition definition, ToolExecutionAccumulator results)
    {
        if (definition.Form != AiBlockKinds.Choice)
            return null;
        return definition.Options.SelectMany(option => option.TargetIds ?? [])
            .Any(id => !results.ReadHabitItems.ContainsKey(id))
            ? "Every option target_ids entry must be a habit ID that query_habits returned in this turn. Read the habits first or omit target_ids."
            : null;
    }

    /// <summary>
    /// After a person answered a choice about records, a new question about the same records asks the same
    /// decision again. A question about records that the answer does not cover stays allowed.
    /// </summary>
    private static string? RepeatedDecisionError(JsonElement args, AiBlockDefinition definition, ToolExecutionAccumulator results)
    {
        if (results.AnsweredTargetIds.Count == 0
            || JsonArgumentParser.GetOptionalString(args, "missing_input") is not ("record_selection" or "bulk_selection"))
            return null;
        var targets = (definition.Form == AiBlockKinds.Choice
                ? definition.Options.SelectMany(AiChoiceSelection.TargetsOf)
                : definition.Items.Select(item => item.Id))
            .Select(target => Guid.TryParse(target, out var id) ? id : Guid.Empty).ToArray();
        return targets.Length > 0 && targets.All(id => id != Guid.Empty && !results.AnsweredTargetIds.Contains(id))
            ? null
            : "The person already answered this decision, and the answer covers these records. Do not ask it again, from another side or as a confirmation. Prepare the approval preview with the matching write tools from the validated choice selection.";
    }
}
