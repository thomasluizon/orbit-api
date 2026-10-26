using Orbit.Domain.Common;

namespace Orbit.Domain.ValueObjects;

public static class HabitPlacement
{
    public const string SubHabit = "sub_habit";
    public const string ChecklistItem = "checklist_item";
    public const string NewParent = "new_parent";

    public static bool IsValid(string? placement) => placement is SubHabit or ChecklistItem or NewParent;

    public static Result Validate(string? placement, Guid? destinationId) =>
        !IsValid(placement) || placement != NewParent && (destinationId is null || destinationId == Guid.Empty)
            ? Result.Failure("invalid_placement") : Result.Success();
}
