using Orbit.Application.Common;

namespace Orbit.Application.Chat.Models;

public sealed record HabitCapacityFailure(string Error, IReadOnlyList<object?> Arguments)
{
    public string ErrorCode => ErrorCodes.HabitLimitReached;
}
