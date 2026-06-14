namespace Orbit.Domain.Enums;

/// <summary>
/// The status change a goal edit caused. Returned by <see cref="Entities.Goal.Update"/> so the caller
/// runs the completion pipeline once on <see cref="Completed"/> and reopens cleanly on <see cref="Reopened"/>.
/// </summary>
public enum GoalEditTransition
{
    None,
    Completed,
    Reopened
}
