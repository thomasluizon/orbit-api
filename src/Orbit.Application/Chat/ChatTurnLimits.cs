namespace Orbit.Application.Chat;

public static class ChatTurnLimits
{
    public const int MaxToolIterations = 5;
    public const int MaxReadCallsPerRound = 4;
    public const int MaxPendingPreviews = 3;
    public const int MaxPromptTags = 50;
    public const int MaxPromptGoals = 50;

    /// <summary>
    /// Budgets the initial completion, each round's parallel AI tools and continuation,
    /// and a transaction allowance for every phase.
    /// </summary>
    public static TimeSpan BlockAnswerTimeout(TimeSpan completionTimeout, TimeSpan transactionTimeout) =>
        (1 + 2 * MaxToolIterations) * (completionTimeout + transactionTimeout);
}
