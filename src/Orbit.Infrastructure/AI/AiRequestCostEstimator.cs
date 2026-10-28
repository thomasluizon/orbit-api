using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.ML.Tokenizers;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

public interface IAiRequestCostEstimator
{
    Task<long> EstimateMicroDollarsAsync(string requestJson, CancellationToken cancellationToken);
}

public sealed class AiRequestCostEstimator(IMemoryCache cache, IOptions<AiSettings> settings) : IAiRequestCostEstimator
{
    private static readonly Lazy<TiktokenTokenizer> Tokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));

    public Task<long> EstimateMicroDollarsAsync(string requestJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = JsonDocument.Parse(requestJson);
        var model = request.RootElement.GetProperty("model").GetString() ?? throw new InvalidOperationException("An AI request requires a model.");
        var price = GetPrice(settings.Value, model);
        var outputTokens = request.RootElement.GetProperty("max_output_tokens").GetInt32();
        if (outputTokens <= 0)
            throw new InvalidOperationException("AI output must have a positive token limit.");
        var inputTokens = EstimateInputTokens(request.RootElement);
        var inputRate = Math.Max(price.InputPerMillionUsd, price.CacheWritePerMillionUsd ?? price.InputPerMillionUsd);
        return Task.FromResult(checked((long)decimal.Ceiling(inputTokens * inputRate + outputTokens * price.OutputPerMillionUsd)));
    }

    internal long EstimateInputTokens(JsonElement request)
    {
        var model = request.GetProperty("model").GetString()!;
        long tokens = 1024;
        foreach (var field in request.EnumerateObject())
        {
            if (field.Name == "tools")
                tokens = checked(tokens + CountPrefix(field.Value, model));
            else if (field.Name == "input" && field.Value.ValueKind == JsonValueKind.Array)
            {
                var first = true;
                foreach (var item in field.Value.EnumerateArray())
                {
                    tokens = checked(tokens + 64 + (first && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("role", out var role) && role.GetString() == "system"
                        ? CountPrefix(item, model) : CountVariableInput(item, model)));
                    first = false;
                }
            }
            else
                tokens = checked(tokens + CountText(field.Value.GetRawText(), model));
        }
        return tokens;
    }

    private long CountPrefix(JsonElement prefix, string model)
    {
        var json = prefix.GetRawText();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return cache.GetOrCreate($"ai-reservation:{model}:{digest}", entry =>
        {
            entry.SetSize(1).SetAbsoluteExpiration(TimeSpan.FromHours(1));
            var count = CountText(json, model);
            return checked((long)decimal.Ceiling(count * 1.25m) + 256);
        });
    }

    /// <summary>
    /// Images use the accepted patch
    /// ceiling and the largest documented multiplier, rounded up with framing room:
    /// https://developers.openai.com/api/docs/guides/images-vision
    /// </summary>
    private static long CountVariableInput(JsonElement input, string model)
    {
        if (input.ValueKind == JsonValueKind.Object)
        {
            if (input.TryGetProperty("type", out var type) && type.GetString() == "input_image")
                return 75_000;
            return input.EnumerateObject().Sum(field => checked(CountText(field.Name, model) + 8 + CountVariableInput(field.Value, model)));
        }
        if (input.ValueKind == JsonValueKind.Array)
            return input.EnumerateArray().Sum(item => checked(8 + CountVariableInput(item, model)));
        return CountText(input.GetRawText(), model);
    }

    private static long CountText(string text, string model)
    {
        var knownEncoding = model.StartsWith("gpt-6", StringComparison.Ordinal)
            || model.StartsWith("gpt-5", StringComparison.Ordinal)
            || model.StartsWith("gpt-4.1", StringComparison.Ordinal);
        return knownEncoding ? Tokenizer.Value.CountTokens(text) : Encoding.UTF8.GetByteCount(text);
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
