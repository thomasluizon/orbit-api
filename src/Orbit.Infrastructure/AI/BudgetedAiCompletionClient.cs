using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.AI;

public sealed class BudgetedAiCompletionClient(AiCompletionClient inner, IAiDailyBudget budget,
    IHttpContextAccessor http, ILogger<BudgetedAiCompletionClient> logger, IAiRequestCostEstimator estimates)
{
    internal AiCompletionClient.CompletionCancellation CreateCompletionCancellation(CancellationToken token, bool streaming = false)
        => inner.CreateCompletionCancellation(token, streaming);
    internal AiCompletionClient Inner => inner;
    internal string ChatModel => inner.ChatModel;

    public async Task<T> RunAsync<T>(Guid? userId, Func<AiCompletionClient, Task<T>> complete,
        CancellationToken cancellationToken, string requestJson)
    {
        userId ??= AiCallContext.Current?.UserId;
        if (userId is null && Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authenticatedUser))
            userId = authenticatedUser;
        if (userId is null || userId == Guid.Empty)
            throw new InvalidOperationException("An authenticated user is required for an AI call.");
        var reservedMicroDollars = await estimates.EstimateMicroDollarsAsync(requestJson, cancellationToken);
        var reservation = await budget.ReserveAsync(userId.Value, reservedMicroDollars, cancellationToken);
        using var context = AiCallContext.Begin(userId.Value);
        try
        {
            return await complete(inner);
        }
        finally
        {
            using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var chargedMicroDollars = context.ChargedMicroDollars ?? (context.HasUnreportedDispatch ? reservedMicroDollars : 0);
            try { await budget.SettleAsync(reservation, chargedMicroDollars, settlement.Token); }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "AI budget settlement failed for {CallId}; spend reservation retained", reservation);
            }
        }
    }

    public async Task<string?> CompleteTextAsync(string systemPrompt, string userPrompt, double temperature = 0.7,
        int? maxOutputTokens = null, string purpose = "text", AiModelTier tier = AiModelTier.Primary,
        CancellationToken cancellationToken = default)
    {
        using var completionCancellation = inner.CreateCompletionCancellation(cancellationToken);
        cancellationToken = completionCancellation.Token;
        var outputTokens = maxOutputTokens ?? AiCompletionClient.DefaultMaxOutputTokens;
        var request = inner.SerializeTextRequest(systemPrompt, userPrompt, temperature, outputTokens, tier);
        return await RunAsync(null, client => client.CompleteTextAsync(systemPrompt, userPrompt, temperature,
            outputTokens, purpose, tier, cancellationToken), cancellationToken, request);
    }

    public async Task<T?> CompleteJsonAsync<T>(string systemPrompt, string userPrompt, double temperature = 0.1,
        int? maxOutputTokens = null, string purpose = "json", AiModelTier tier = AiModelTier.Primary,
        Guid? userId = null, CancellationToken cancellationToken = default)
    {
        using var completionCancellation = inner.CreateCompletionCancellation(cancellationToken);
        cancellationToken = completionCancellation.Token;
        var outputTokens = maxOutputTokens ?? AiCompletionClient.DefaultMaxOutputTokens;
        var request = inner.SerializeTextRequest(systemPrompt, userPrompt, temperature, outputTokens, tier, userId, json: true);
        return await RunAsync(userId, client => client.CompleteJsonAsync<T>(systemPrompt, userPrompt, temperature,
            outputTokens, purpose, tier, userId, cancellationToken), cancellationToken, request);
    }
}
