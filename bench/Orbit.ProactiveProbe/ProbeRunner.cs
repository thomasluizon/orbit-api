#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.ProactiveProbe;

/// <summary>
/// Sends the owner's two habits through the real <see cref="AiProactiveCheckinMessageService"/> to the provider, with the eval key.
/// The service builds the real system prompt and user prompt and runs its grounding check on the answer.
/// The probe reports the HTTP status, the raw answer, the grounding result, the tokens and the cost of each call.
/// A dry run reports the prompt tokens and the estimate, and makes no call.
/// </summary>
internal static class ProbeRunner
{
    public const string KeyVariable = "ORBIT_PROACTIVE_PROBE_API_KEY";
    public const decimal CostCeilingUsd = 0.02m;
    private const string Purpose = "proactive_item";
    private const int MaxOutputTokens = 512;
    private const string OwnerWater = "Water - 1.18L";
    private const string OwnerEmail = "Change twitter founder account email";

    private sealed record Plan(string Language, ProactiveTrigger Trigger);

    private static readonly Plan[] Plans =
    [
        new("pt-BR", ProactiveTrigger.MorningPlan), new("pt-BR", ProactiveTrigger.MorningPlan), new("pt-BR", ProactiveTrigger.MorningPlan),
        new("en", ProactiveTrigger.EveningCheckin), new("en", ProactiveTrigger.EveningCheckin), new("en", ProactiveTrigger.EveningCheckin)
    ];

    private sealed record Totals(int Calls, int Failures, int Grounded, decimal Estimate, decimal Cost);

    public static async Task<int> RunAsync(bool dryRun)
    {
        var model = Environment.GetEnvironmentVariable("ORBIT_PROACTIVE_PROBE_MODEL") is { Length: > 0 } configured ? configured : "gpt-6-luna";
        var baseUrl = Environment.GetEnvironmentVariable("ORBIT_PROACTIVE_PROBE_BASE_URL") is { Length: > 0 } url ? url : "https://api.openai.com/v1";
        var key = Environment.GetEnvironmentVariable(KeyVariable);
        if (!dryRun && string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine($"PROBE_ERROR {KeyVariable} is not set. Set it to the eval key, or pass --dry-run.");
            return 2;
        }
        var settings = LoadSettings(baseUrl, model);
        var price = settings.Pricing[model];
        var cacheWrite = price.CacheWritePerMillionUsd ?? price.InputPerMillionUsd;
        Console.WriteLine($"PROBE model={model} mode={(dryRun ? "dry-run" : "live")} calls={Plans.Length} max_output_tokens={MaxOutputTokens} ceiling_usd={Text(CostCeilingUsd)}");
        Console.WriteLine($"PROBE prices cache_write_per_million_usd={Text(cacheWrite)} output_per_million_usd={Text(price.OutputPerMillionUsd)}");

        var logs = new LogCapture();
        var usage = new TokenUsageLedger();
        var response = new ResponseCapture();
        var service = dryRun ? null : BuildService(settings, key!, model, logs, usage, response);
        var totals = new Totals(0, 0, 0, 0m, 0m);
        for (var index = 0; index < Plans.Length; index++)
        {
            var plan = Plans[index];
            var facts = new ProactiveFacts(plan.Trigger, [OwnerWater, OwnerEmail], HabitCount: 2);
            var promptTokens = O200kTokenCounter.Shared.CountTokens(
                AiProactiveCheckinMessageService.BuildSystemPrompt(plan.Language) + AiProactiveCheckinMessageService.BuildPrompt(facts, plan.Language));
            var estimate = (promptTokens * cacheWrite + MaxOutputTokens * price.OutputPerMillionUsd) / 1_000_000m;
            Console.WriteLine($"PLAN call={index + 1} language={plan.Language} trigger={plan.Trigger} prompt_tokens={promptTokens} estimate_usd={Text(estimate)}");
            totals = totals with { Estimate = totals.Estimate + estimate };
            if (service is null)
                continue;
            if (totals.Cost + estimate > CostCeilingUsd)
            {
                Console.WriteLine($"PROBE_STOP call={index + 1} reason=\"the call could take the total past {Text(CostCeilingUsd)} USD\"");
                break;
            }
            totals = await CallAsync(service, index + 1, plan, facts, (cacheWrite, price.OutputPerMillionUsd), totals, logs, usage, response);
        }
        Console.WriteLine($"PROBE calls={totals.Calls} grounded={totals.Grounded} refused_or_failed={totals.Calls - totals.Grounded} http_failures={totals.Failures} estimate_usd={Text(totals.Estimate)} cost_usd={Text(totals.Cost)}");
        return totals.Failures == 0 ? 0 : 1;
    }

    private static async Task<Totals> CallAsync(AiProactiveCheckinMessageService service, int number, Plan plan, ProactiveFacts facts,
        (decimal CacheWrite, decimal Output) prices, Totals totals, LogCapture logs, TokenUsageLedger usage, ResponseCapture response)
    {
        logs.Clear();
        usage.Reset();
        response.Reset();
        totals = totals with { Calls = totals.Calls + 1 };
        ProactiveMessage message;
        using (AiCallContext.Begin(Guid.NewGuid()))
            message = await service.GenerateAsync(facts, plan.Language, facts.HabitTitles, CancellationToken.None);

        var raw = response.AssistantText();
        var (habitTitles, goalTitles) = RawTitles(raw);
        var refusal = logs.Property("Reason");
        var failure = logs.Failure;
        var grounded = refusal is null && failure is null;
        var result = grounded ? $"grounded text={JsonSerializer.Serialize(message.Text)}"
            : failure is not null ? $"generation_failed error={JsonSerializer.Serialize(failure.GetType().Name + ": " + failure.Message)} fallback={JsonSerializer.Serialize(message.Text)}"
            : $"refused reason={refusal} fallback={JsonSerializer.Serialize(message.Text)}";
        var cost = (usage.InputTokens * prices.CacheWrite + usage.OutputTokens * prices.Output) / 1_000_000m;
        var http = response.Status;
        Console.WriteLine($"CALL call={number} language={plan.Language} trigger={plan.Trigger} http={http} text={JsonSerializer.Serialize(raw)} habitTitles={habitTitles} goalTitles={goalTitles} result={result} input_tokens={usage.InputTokens} output_tokens={usage.OutputTokens} cost_usd={Text(cost)}");
        return totals with
        {
            Cost = totals.Cost + cost,
            Failures = totals.Failures + (http is >= 200 and < 300 ? 0 : 1),
            Grounded = totals.Grounded + (grounded ? 1 : 0)
        };
    }

    private static (string HabitTitles, string GoalTitles) RawTitles(string? raw)
    {
        if (raw is null)
            return ("null", "null");
        try
        {
            using var document = JsonDocument.Parse(raw);
            string Array(string name) => document.RootElement.TryGetProperty(name, out var value) ? value.GetRawText() : "null";
            return (Array("habitTitles"), Array("goalTitles"));
        }
        catch (JsonException)
        {
            return ("unparsed", "unparsed");
        }
    }

    private static AiProactiveCheckinMessageService BuildService(AiSettings settings, string key, string model, LogCapture logs,
        TokenUsageLedger usage, ResponseCapture response)
    {
        var options = new ResponsesClientOptions
        {
            Endpoint = new Uri(settings.BaseUrl),
            NetworkTimeout = TimeSpan.FromSeconds(settings.BufferedNetworkTimeoutSeconds),
            RetryPolicy = new ClientRetryPolicy(0)
        };
        options.AddPolicy(response, PipelinePosition.PerCall);
        var completion = new AiCompletionClient(new ResponsesClient(new ApiKeyCredential(key), options),
            logs.For<AiCompletionClient>(), usage, primaryModel: model, subTaskModel: model);
        var budgeted = new BudgetedAiCompletionClient(completion, new NoBudgetLedger(), new HttpContextAccessor(),
            logs.For<BudgetedAiCompletionClient>(),
            new AiRequestCostEstimator(new MemoryCache(new MemoryCacheOptions()), Options.Create(settings)));
        return new AiProactiveCheckinMessageService(budgeted, logs.For<AiProactiveCheckinMessageService>());
    }

    private static string Text(decimal value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>The AI settings of <c>appsettings.json</c> in this checkout, with the probe base URL and model.</summary>
    private static AiSettings LoadSettings(string baseUrl, string model)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("Run the probe inside the orbit-api checkout, so it can read the prices of appsettings.json.");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(directory.FullName, "src", "Orbit.Api", "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI:BaseUrl"] = baseUrl, ["AI:Model"] = model, ["AI:SubTaskModel"] = model })
            .Build();
        var settings = configuration.GetSection(AiSettings.SectionName).Get<AiSettings>()
            ?? throw new InvalidOperationException("appsettings.json has no AI section.");
        return settings.Pricing.ContainsKey(model) ? settings
            : throw new InvalidOperationException($"appsettings.json has no AI:Pricing entry for {model}.");
    }
}
