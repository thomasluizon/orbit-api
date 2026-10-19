using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.AI;

public sealed class BudgetedAiCompletionClient(AiCompletionClient inner, IAiDailyBudget budget,
    IHttpContextAccessor http, ILogger<BudgetedAiCompletionClient> logger)
{
    internal CancellationTokenSource CreateCompletionCancellation(CancellationToken token) => inner.CreateCompletionCancellation(token);
    internal string ChatModel => inner.ChatModel;

    public async Task<T> RunAsync<T>(Guid? userId, Func<AiCompletionClient, Task<T>> complete, CancellationToken cancellationToken,
        long reservedTokens = AppConstants.MaxAiRequestTotalTokens)
    {
        userId ??= AiCallContext.Current?.UserId;
        if (userId is null && Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authenticatedUser))
            userId = authenticatedUser;
        if (userId is null || userId == Guid.Empty)
            throw new InvalidOperationException("An authenticated user is required for an AI call.");
        var reservation = await budget.ReserveAsync(userId.Value, reservedTokens, cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        var options = new ChatCompletionOptions { MaxOutputTokenCount = maxOutputTokens ?? AiTokenReservation.DefaultMaxOutputTokens };
        var reservation = AiTokenReservation.Estimate([new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)], options);
        return RunAsync(null, client => client.CompleteTextAsync(systemPrompt, userPrompt, temperature,
            options.MaxOutputTokenCount, purpose, tier, cancellationToken), cancellationToken, reservation);
    }

    public Task<T?> CompleteJsonAsync<T>(string systemPrompt, string userPrompt, double temperature = 0.1,
        int? maxOutputTokens = null, string purpose = "json", AiModelTier tier = AiModelTier.Primary,
        Guid? userId = null, CancellationToken cancellationToken = default)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = maxOutputTokens ?? AiTokenReservation.DefaultMaxOutputTokens,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };
        var reservation = AiTokenReservation.Estimate([new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)], options);
        return RunAsync(userId, client => client.CompleteJsonAsync<T>(systemPrompt, userPrompt, temperature,
            options.MaxOutputTokenCount, purpose, tier, userId, cancellationToken), cancellationToken, reservation);
    }
}
