using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.AI;

public sealed class BudgetedAiCompletionClient(AiCompletionClient inner, IAiDailyBudget budget,
    IHttpContextAccessor http, ILogger<BudgetedAiCompletionClient> logger)
{
    internal CancellationTokenSource CreateCompletionCancellation(CancellationToken token) => inner.CreateCompletionCancellation(token);
    internal string ChatModel => inner.ChatModel;

    public async Task<T> RunAsync<T>(Guid? userId, Func<AiCompletionClient, Task<T>> complete, CancellationToken cancellationToken)
    {
        userId ??= AiCallContext.Current?.UserId;
        if (userId is null && Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authenticatedUser))
            userId = authenticatedUser;
        if (userId is null || userId == Guid.Empty)
            throw new InvalidOperationException("An authenticated user is required for an AI call.");
        var reservation = await budget.ReserveAsync(userId.Value, cancellationToken);
        using var context = AiCallContext.Begin(userId.Value);
        try
        {
            return await complete(inner);
        }
        finally
        {
            using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await budget.SettleAsync(reservation, context.Tokens, settlement.Token); }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "AI budget settlement failed for {CallId}; token reservation retained", reservation);
            }
        }
    }

    public Task<string?> CompleteTextAsync(string systemPrompt, string userPrompt, double temperature = 0.7,
        int? maxOutputTokens = null, string purpose = "text", AiModelTier tier = AiModelTier.Primary,
        CancellationToken cancellationToken = default) =>
        RunAsync(null, client => client.CompleteTextAsync(systemPrompt, userPrompt, temperature, maxOutputTokens, purpose, tier, cancellationToken), cancellationToken);

    public Task<T?> CompleteJsonAsync<T>(string systemPrompt, string userPrompt, double temperature = 0.1,
        int? maxOutputTokens = null, string purpose = "json", AiModelTier tier = AiModelTier.Primary,
        Guid? userId = null, CancellationToken cancellationToken = default) =>
        RunAsync(userId, client => client.CompleteJsonAsync<T>(systemPrompt, userPrompt, temperature, maxOutputTokens, purpose, tier, userId, cancellationToken), cancellationToken);
}
