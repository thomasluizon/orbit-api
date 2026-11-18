#pragma warning disable OPENAI001

using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Infrastructure.AI;

namespace Orbit.ChangeListProbe;

/// <summary>
/// Sends the real declarations of each declaration shape and each client catalog to the provider once, with the eval key,
/// and reports the HTTP status, the provider error text, the input tokens and the cost of each call. A dry run reports
/// the declaration tokens, the schema string size, the depth and the estimate, and makes no call.
/// </summary>
internal static class ProbeRunner
{
    public const int MaxCalls = 6;
    public const int MaxOutputTokens = 128;
    public const string KeyVariable = "ORBIT_CHANGE_PROBE_API_KEY";

    private sealed record Totals(int Calls, int Failures, decimal Estimate, decimal Cost);

    public static async Task<int> RunAsync(IReadOnlyList<ChangeDeclarationShape> shapes, bool dryRun)
    {
        var model = Environment.GetEnvironmentVariable("ORBIT_CHANGE_PROBE_MODEL") is { Length: > 0 } configured ? configured : "gpt-6-luna";
        var baseUrl = Environment.GetEnvironmentVariable("ORBIT_CHANGE_PROBE_BASE_URL") is { Length: > 0 } url ? url : "https://api.openai.com/v1";
        var key = Environment.GetEnvironmentVariable(KeyVariable);
        if (!dryRun && string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine($"PROBE_ERROR {KeyVariable} is not set. Set it to the eval key, or pass --dry-run.");
            return 2;
        }
        var (cacheWrite, output) = Prices(model);
        var client = dryRun ? null : new ResponsesClient(new ApiKeyCredential(key!), new ResponsesClientOptions { Endpoint = new Uri(baseUrl) });
        Console.WriteLine($"PROBE model={model} mode={(dryRun ? "dry-run" : "live")} max_calls={MaxCalls} max_output_tokens={MaxOutputTokens}");
        Console.WriteLine($"PROBE prices cache_write_per_million_usd={Text(cacheWrite)} output_per_million_usd={Text(output)}");
        var totals = new Totals(0, 0, 0m, 0m);
        foreach (var shape in shapes)
            foreach (var catalogClient in ChangeListCatalogs.Clients)
                totals = await ProbeAsync(await ChangeListCatalogs.BuildAsync(shape, catalogClient), client, model, (cacheWrite, output), totals);
        Console.WriteLine($"PROBE calls={totals.Calls} failures={totals.Failures} estimate_usd={Text(totals.Estimate)} cost_usd={Text(totals.Cost)}");
        return totals.Failures == 0 ? 0 : 1;
    }

    private static async Task<Totals> ProbeAsync(ProbeCatalog catalog, ResponsesClient? client, string model,
        (decimal CacheWrite, decimal Output) prices, Totals totals)
    {
        var parameters = catalog.Parameters().ToArray();
        var tokens = O200kTokenCounter.Shared.CountTokens(catalog.ToolsJson);
        var size = parameters.Sum(tool => ChangeListCatalogs.SchemaStringLength(tool.Parameters));
        var depth = parameters.Max(tool => ChangeListCatalogs.ObjectDepth(tool.Parameters));
        var estimate = (tokens * prices.CacheWrite + MaxOutputTokens * prices.Output) / 1_000_000m;
        Console.WriteLine($"CATALOG shape={catalog.Shape} client={catalog.Client} tools={catalog.Tools.Count} declaration_tokens={tokens} schema_string_size={size} max_depth={depth} estimate_usd={Text(estimate)}");
        totals = totals with { Estimate = totals.Estimate + estimate };
        if (client is null || totals.Calls >= MaxCalls)
            return totals;
        totals = totals with { Calls = totals.Calls + 1 };
        var options = new CreateResponseOptions
        {
            Model = model,
            StoredOutputEnabled = false,
            MaxOutputTokenCount = MaxOutputTokens,
            ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.Low }
        };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem("Reply with the single word OK. Call no tool."));
        foreach (var tool in catalog.Tools.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            options.Tools.Add(tool.Value);
        try
        {
            var response = (await client.CreateResponseAsync(options)).Value;
            var input = response.Usage?.InputTokenCount ?? 0;
            var produced = response.Usage?.OutputTokenCount ?? 0;
            var cost = (input * prices.CacheWrite + produced * prices.Output) / 1_000_000m;
            Console.WriteLine($"CALL shape={catalog.Shape} client={catalog.Client} http=200 result=pass response_status={response.Status} input_tokens={input} output_tokens={produced} cost_usd={Text(cost)}");
            return totals with { Cost = totals.Cost + cost };
        }
        catch (ClientResultException exception)
        {
            var body = exception.GetRawResponse()?.Content?.ToString() ?? string.Empty;
            Console.WriteLine($"CALL shape={catalog.Shape} client={catalog.Client} http={exception.Status} result=fail error={JsonSerializer.Serialize(ErrorText(body) ?? exception.Message)}");
            return totals with { Failures = totals.Failures + 1 };
        }
    }

    private static string Text(decimal value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>The cache-write and output prices of <paramref name="model"/> from the API settings of this checkout.</summary>
    private static (decimal CacheWrite, decimal Output) Prices(string model)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("Run the probe inside the orbit-api checkout, so it can read the prices of appsettings.json.");
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "src", "Orbit.Api", "appsettings.json")));
        var price = settings.RootElement.GetProperty("AI").GetProperty("Pricing").GetProperty(model);
        return (price.GetProperty("CacheWritePerMillionUsd").GetDecimal(), price.GetProperty("OutputPerMillionUsd").GetDecimal());
    }

    private static string? ErrorText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var text)
                ? text.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
