using Orbit.Domain.Common;

namespace Orbit.Infrastructure.AI;

public sealed class AiBudgetExceededException() : Exception(Error.Message)
{
    public static AppError Error { get; } = new("AI_DAILY_BUDGET_EXCEEDED", "Your daily AI safety budget has been reached. Please try again tomorrow.");
}
