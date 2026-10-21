using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

public interface IAiRequestCostEstimator
{
    Task<long> EstimateMicroDollarsAsync(string requestJson, CancellationToken cancellationToken);
}

public sealed class AiRequestCostEstimator(AiCompletionClient client, IOptions<AiSettings> settings) : IAiRequestCostEstimator
{
    public async Task<long> EstimateMicroDollarsAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var request = JsonDocument.Parse(requestJson);
        var model = request.RootElement.GetProperty("model").GetString() ?? throw new InvalidOperationException("An AI request requires a model.");
        var price = GetPrice(settings.Value, model);
        var outputTokens = request.RootElement.GetProperty("max_output_tokens").GetInt32();
        if (outputTokens <= 0)
            throw new InvalidOperationException("AI output must have a positive token limit.");
        var inputTokens = await client.CountInputTokensAsync(requestJson, cancellationToken);
        var inputRate = Math.Max(price.InputPerMillionUsd, price.CacheWritePerMillionUsd ?? price.InputPerMillionUsd);
        return checked((long)decimal.Ceiling(inputTokens * inputRate + outputTokens * price.OutputPerMillionUsd));
    }

    internal static AiModelPrice GetPrice(AiSettings settings, string model)
    {
        if (!settings.Pricing.TryGetValue(model, out var price)
            || price.InputPerMillionUsd <= 0 || price.OutputPerMillionUsd <= 0
            || price.CachedInputPerMillionUsd < 0 || price.CacheWritePerMillionUsd < 0)
            throw new InvalidOperationException($"AI pricing is missing or invalid for model '{model}'.");
        return price;
    }
}
