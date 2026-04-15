using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// Soft budget governing a single chat turn. Includes input/output token
/// ceilings and a max tool-call iteration count. When any limit is exceeded
/// the intent service short-circuits and returns a polite "slow down" failure
/// rather than continuing to spend tokens.
/// </summary>
/// <param name="MaxInputTokens">Cumulative input tokens across the whole turn.</param>
/// <param name="MaxOutputTokens">Cumulative output tokens across the whole turn.</param>
/// <param name="MaxToolIterations">Max number of tool-call rounds.</param>
public readonly record struct AiBudget(
    int MaxInputTokens,
    int MaxOutputTokens,
    int MaxToolIterations)
{
    public static AiBudget Default { get; } = new(
        MaxInputTokens: 40_000,
        MaxOutputTokens: 8_000,
        MaxToolIterations: 5);
}

/// <summary>
/// Snapshot of cumulative token spend across a single chat turn.
/// </summary>
public sealed class AiBudgetTracker
{
    private readonly AiBudget _budget;

    public AiBudgetTracker(AiBudget budget)
    {
        _budget = budget;
    }

    public int InputTokensUsed { get; private set; }
    public int OutputTokensUsed { get; private set; }
    public int ToolIterations { get; private set; }
    public AiBudget Budget => _budget;

    public void AddUsage(int inputTokens, int outputTokens)
    {
        if (inputTokens > 0) InputTokensUsed += inputTokens;
        if (outputTokens > 0) OutputTokensUsed += outputTokens;
    }

    public void IncrementIteration()
    {
        ToolIterations++;
    }

    /// <summary>
    /// Returns a short reason when any budget limit is exceeded; otherwise null.
    /// </summary>
    public string? GetExceededReason()
    {
        if (InputTokensUsed > _budget.MaxInputTokens)
            return $"input_tokens_exceeded ({InputTokensUsed}>{_budget.MaxInputTokens})";
        if (OutputTokensUsed > _budget.MaxOutputTokens)
            return $"output_tokens_exceeded ({OutputTokensUsed}>{_budget.MaxOutputTokens})";
        if (ToolIterations > _budget.MaxToolIterations)
            return $"tool_iterations_exceeded ({ToolIterations}>{_budget.MaxToolIterations})";
        return null;
    }
}

public interface IAiIntentService
{
    Task<Result<AiResponse>> SendWithToolsAsync(
        string userMessage,
        string systemPrompt,
        IReadOnlyList<object> toolDeclarations,
        byte[]? imageData = null,
        string? imageMimeType = null,
        IReadOnlyList<ChatHistoryMessage>? history = null,
        AiBudgetTracker? budget = null,
        CancellationToken cancellationToken = default);

    Task<Result<AiResponse>> ContinueWithToolResultsAsync(
        AiConversationContext conversationContext,
        IReadOnlyList<AiToolCallResult> results,
        AiBudgetTracker? budget = null,
        CancellationToken cancellationToken = default);
}
