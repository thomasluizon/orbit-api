using System.Text.Json;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat;

public static class PendingOperationPlacementNegotiation
{
    public static bool RequiresSupport(string operationId, JsonElement arguments)
    {
        if (operationId == AccountChangeSet.OperationName)
            return (AccountChangeSet.Read(arguments) ?? []).Any(member => RequiresSupport(member.OperationId, member.Arguments));
        if (operationId == "bulk_create_habits")
            return arguments.TryGetProperty("habits", out var habits) && habits.ValueKind == JsonValueKind.Array
                && habits.EnumerateArray().Any(HasNonDefaultPlacement);
        return operationId is "create_habit" or "create_sub_habit" && HasNonDefaultPlacement(arguments);
    }

    private static bool HasNonDefaultPlacement(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (item.TryGetProperty(HabitPlacementRecommendation.Argument, out var placement)
            && placement.ValueKind != JsonValueKind.Null
            && (placement.ValueKind != JsonValueKind.String || placement.GetString() != HabitPlacement.SubHabit))
            return true;
        return item.TryGetProperty("sub_habits", out var children) && children.ValueKind == JsonValueKind.Array
            && children.EnumerateArray().Any(HasNonDefaultPlacement);
    }
}
