#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using OpenAI.Responses;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiInputBudgetTests
{
    [Fact]
    public async Task Plan_TwoToolTokenDeltaFitsExistingWarmCallCeiling()
    {
        var request = await BuildCacheRequestAsync("composition", User.Create("Person", "plan@example.com").Value,
            null, message: "Show my habits and goals");
        var baseline = request with
        {
            SystemPrompt = request.SystemPrompt.Replace(PlanToolDeclaration.Instruction, "", StringComparison.Ordinal),
            ToolDeclarations = request.ToolDeclarations.Where(declaration => JsonSerializer.SerializeToElement(declaration)
                .GetProperty("name").GetString() != PlanToolDeclaration.Name).ToArray()
        };
        var transport = new SequenceHandler(ResponsesTestData.Text("Done"));
        var service = BuildService(transport);
        (await service.SendWithToolsAsync(baseline)).IsSuccess.Should().BeTrue();
        (await service.SendWithToolsAsync(request)).IsSuccess.Should().BeTrue();
        var before = transport.Bodies[0];
        var after = transport.Bodies[1];
        var inputs = Count(ModelInput(after));
        var inputDelta = inputs - Count(ModelInput(before));
        var empty = JsonSerializer.SerializeToElement(new { orbit_read_complete = true });
        AiToolCall[] actions = [new("query_habits", "habits", empty), new("query_goals", "goals", empty)];
        var planCall = new AiToolCall(PlanToolDeclaration.Name, "plan", JsonSerializer.SerializeToElement(new
        {
            title = "Your progress",
            steps = new[] { new { text = "Read habits", tool = "query_habits", phase = "execution" }, new { text = "Read goals", tool = "query_goals", phase = "execution" } }
        }));
        var outputBefore = Count(string.Concat(actions.Select(PlanOutput)));
        var outputAfter = Count(string.Concat(new[] { planCall }.Concat(actions).Select(PlanOutput)));
        var settings = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build().GetSection("AI").Get<AiSettings>()!;
        var price = AiRequestCostEstimator.GetPrice(settings, "gpt-6-luna");
        var shared = CachedPrefixTokens([CostStaticPrefix(after)], after);
        var cached = shared >= 1024 ? shared / 128 * 128 : 0;
        var cost = ((inputs - cached) * price.CacheWritePerMillionUsd!.Value + cached * price.CachedInputPerMillionUsd
            + outputAfter * price.OutputPerMillionUsd) / 1_000_000;
        var report = FormattableString.Invariant($"o200k_base, SDK-serialized request and function calls; input delta {inputDelta}; output delta {outputAfter - outputBefore}; input {inputs}; cached {cached}; output {outputAfter}; warm two-tool call USD {cost:F8}.");
        output.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "orbit-1976-plan-cost.txt"), report);
        inputDelta.Should().BePositive();
        cost.Should().BeLessThanOrEqualTo(0.0009m, report);
    }

    private static string PlanOutput(AiToolCall call) => ModelReaderWriter.Write(ResponseItem.CreateFunctionCallItem(
        call.Id, call.Name, BinaryData.FromString(call.Args.GetRawText()))).ToString();
}
