using static Orbit.Application.Chat.Tools.ToolArgumentNames;
using System.Text.Json;
using System.Text;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed partial class PendingOperationChangePreviewer
{
    private static IEnumerable<JsonElement> CreationNodes(JsonElement habits)
    {
        foreach (var habit in habits.EnumerateArray())
        {
            if (habit.ValueKind != JsonValueKind.Object) continue;
            yield return habit;
            if (habit.TryGetProperty(SubHabits, out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in CreationNodes(children))
                    yield return child;
        }
    }

    private async Task<string?> BindCreationReuseAsync(Guid userId, JsonElement source, Habit? existing,
        IReadOnlyList<Habit> known, List<PendingOperationChange> fields, CancellationToken ct)
    {
        var fingerprint = existing is null ? string.Empty : FingerprintHabit(existing);
        if (existing is not null && !await BindReuseFieldsAsync(userId, source, existing, known, fields, ct))
            return null;
        if (!source.TryGetProperty(SubHabits, out var children) || children.ValueKind != JsonValueKind.Array)
            return fingerprint;
        var state = new StringBuilder(fingerprint);
        var previousChildren = new List<object>();
        foreach (var child in children.EnumerateArray())
        {
            var reused = TryReadId(child, HabitId, out var id) ? known.FirstOrDefault(habit => habit.Id == id) : null;
            var childFields = CreationFields(child);
            var childFingerprint = await BindCreationReuseAsync(userId, child, reused, known, childFields, ct);
            if (childFingerprint is null) return null;
            state.Append(childFingerprint);
            previousChildren.Add(childFields.ToDictionary(field => field.Field, field => field.OldValue));
        }
        if (state.Length > 0)
        {
            var index = fields.FindIndex(field => field.Field == SubHabits);
            if (index >= 0) fields[index] = fields[index] with { OldValue = JsonSerializer.Serialize(previousChildren) };
        }
        return state.ToString();
    }

    private static List<PendingOperationChange> CreationFields(JsonElement source, string name = "") => source.EnumerateObject()
        .Where(property => CreateFields.Contains(property.Name))
        .OrderBy(property => property.Name != "title").ThenBy(property => property.Name, StringComparer.Ordinal)
        .Select(property => new PendingOperationChange(
            Guid.Empty, name, property.Name, null, AgentArgumentPreview.FormatValue(ProactiveProposal.StripMetadata(property.Value)),
            AgentArgumentPreview.ResolveValueType(property.Name, property.Value), ProactiveProposal.StripMetadata(property.Value),
            property.Name != DueDate || !source.TryGetProperty(StartDate, out _))).ToList();

    private async Task<bool> BindReuseFieldsAsync(Guid userId, JsonElement source, Habit existing,
        IReadOnlyList<Habit> known, List<PendingOperationChange> fields, CancellationToken ct)
    {
        var values = HabitValues(existing);
        values[HabitId] = existing.Title;
        values["tag_names"] = values[TagIds] = FormatTags(existing) ?? "[]";
        values[GoalIds] = string.Join(", ", existing.Goals.OrderBy(goal => goal.Id).Select(goal => goal.Title));
        values[ParentHabitId] = known.FirstOrDefault(habit => habit.Id == existing.ParentHabitId)?.Title ?? "No parent";
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            fields[index] = field with { EntityId = existing.Id, EntityName = existing.Title, OldValue = values.GetValueOrDefault(field.Field) };
        }
        PreserveReuseParent(source, fields, existing, values[ParentHabitId]);
        return await BindReuseAssociationsAsync(userId, source, fields, ct);
    }

    private static void PreserveReuseParent(JsonElement source, List<PendingOperationChange> fields,
        Habit existing, string? currentParent)
    {
        if (fields.All(field => field.Field != ParentHabitId))
            fields.Add(new(existing.Id, existing.Title, ParentHabitId, currentParent, currentParent, "text",
                JsonSerializer.SerializeToElement(existing.ParentHabitId), false));
        if (source.TryGetProperty(ParentHabitId, out _)) return;
        var index = fields.FindIndex(field => field.Field == ParentHabitId);
        if (index >= 0 && fields[index].ProposedValue?.ValueKind == JsonValueKind.Null)
            fields[index] = fields[index] with
            {
                NewValue = currentParent,
                ProposedValue = JsonSerializer.SerializeToElement(existing.ParentHabitId),
                IsEditable = false
            };
    }

    private async Task<bool> BindReuseAssociationsAsync(Guid userId, JsonElement source,
        List<PendingOperationChange> fields, CancellationToken ct)
    {
        var tagIds = DependencyIds(source, TagIds);
        var goalIds = DependencyIds(source, GoalIds);
        var tags = tagIds.Count == 0 ? [] : await tagRepository.FindAsync(tag => tag.UserId == userId && tagIds.Contains(tag.Id), ct);
        var goals = goalIds.Count == 0 ? [] : await goalRepository.FindAsync(goal => goal.UserId == userId && goalIds.Contains(goal.Id), ct);
        if (tags.Count != tagIds.Count || goals.Count != goalIds.Count) return false;
        ReplaceAssociationText(fields, TagIds, tags.OrderBy(tag => tag.Id).Select(tag => tag.Name));
        ReplaceAssociationText(fields, GoalIds, goals.OrderBy(goal => goal.Id).Select(goal => goal.Title));
        return true;
    }

    private static void ReplaceAssociationText(List<PendingOperationChange> fields, string name, IEnumerable<string> names)
    {
        var index = fields.FindIndex(field => field.Field == name);
        if (index >= 0) fields[index] = fields[index] with { NewValue = string.Join(", ", names) };
    }
}
