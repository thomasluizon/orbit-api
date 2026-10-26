using System.Globalization;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat;

public sealed record HabitStructurePattern(int Checklists, int SubHabits, int Neither)
{
    public static HabitStructurePattern From(IReadOnlyList<Habit> habits)
    {
        var active = habits.Where(habit => !habit.IsDeleted && !habit.IsCompleted).ToArray();
        var parents = active.Where(habit => habit.ParentHabitId.HasValue)
            .Select(habit => habit.ParentHabitId!.Value).ToHashSet();
        return new(active.Count(habit => habit.ChecklistItems.Count > 0),
            active.Count(habit => parents.Contains(habit.Id)),
            active.Count(habit => !habit.ParentHabitId.HasValue && habit.ChecklistItems.Count == 0 && !parents.Contains(habit.Id)));
    }
}

public static class HabitPlacementRecommendation
{
    public const string Argument = "placement";

    public static PendingOperationPlacement For(Habit destination, IReadOnlyList<Habit> habits, string? selected = null)
    {
        var checklist = destination.ChecklistItems.Count;
        var children = habits.Count(habit => habit.ParentHabitId == destination.Id && !habit.IsDeleted && !habit.IsCompleted);
        var pattern = HabitStructurePattern.From(habits);
        var (recommended, reason) = checklist > 0 || children > 0
            ? checklist >= children ? (HabitPlacement.ChecklistItem, "destinationChecklist") : (HabitPlacement.SubHabit, "destinationSubHabits")
            : pattern.Checklists > 0 || pattern.SubHabits > 0
                ? pattern.Checklists >= pattern.SubHabits ? (HabitPlacement.ChecklistItem, "accountChecklists") : (HabitPlacement.SubHabit, "accountSubHabits")
                : pattern.Neither > 1 ? (HabitPlacement.NewParent, "accountFlat") : (HabitPlacement.SubHabit, "newRoutine");
        selected ??= recommended;
        return new(selected, recommended, "chat.placement." + reason, Copy(reason, checklist, children),
            new[] { HabitPlacement.SubHabit, HabitPlacement.ChecklistItem, HabitPlacement.NewParent }
                .Where(option => option != selected).ToArray(), destination.Id, destination.Title);
    }

    private static IReadOnlyDictionary<string, string> Copy(string reason, int checklist, int children)
    {
        var count = (reason == "destinationChecklist" ? checklist : children).ToString(CultureInfo.InvariantCulture);
        var (en, pt) = reason switch
        {
            "destinationChecklist" when checklist == 1 => ("You already keep a checklist item here.", "Você já tem um item na lista aqui."),
            "destinationChecklist" => ($"You already keep {count} checklist items here.", $"Você já tem {count} itens na lista aqui."),
            "destinationSubHabits" when children == 1 => ("You already keep a sub-habit here.", "Você já tem um sub-hábito aqui."),
            "destinationSubHabits" => ($"You already keep {count} sub-habits here.", $"Você já tem {count} sub-hábitos aqui."),
            "accountChecklists" => ("You usually organize your habits with checklists.", "Você costuma organizar seus hábitos com listas."),
            "accountSubHabits" => ("You usually organize your habits with sub-habits.", "Você costuma organizar seus hábitos com sub-hábitos."),
            "accountFlat" => ("You usually keep your habits separate.", "Você costuma manter seus hábitos separados."),
            _ => ("Sub-habits let you track each part of your routine.", "Sub-hábitos permitem acompanhar cada parte da sua rotina.")
        };
        return new Dictionary<string, string> { ["en"] = en, ["pt-BR"] = pt };
    }
}
