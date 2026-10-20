namespace Orbit.Application.Chat;

public static class ChatTurnLimits
{
    public const int MaxToolIterations = 5;

    /// <summary>
    /// Budgets the initial completion, each round's parallel AI tools and continuation,
    /// and a transaction allowance for every phase.
    /// </summary>
    public static TimeSpan BlockAnswerTimeout(TimeSpan completionTimeout, TimeSpan transactionTimeout) =>
        (1 + 2 * MaxToolIterations) * (completionTimeout + transactionTimeout);
}
