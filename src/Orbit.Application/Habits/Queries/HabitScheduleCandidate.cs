using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Queries;

public sealed record HabitScheduleCandidate(
    HabitScheduleSnapshot Schedule,
    string Title,
    string? Description,
    int? Position,
    IReadOnlyList<Tag> Tags);
