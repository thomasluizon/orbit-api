#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiIntentServiceStreamingTests
{
    private static (AiIntentService Service, AiCompletionClient Client, SequenceSseHandler Handler) BuildChangePlanService(
        string chatReasoningEffort, params string[] bodies)
    {
        var handler = new SequenceSseHandler(bodies);
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usage,
            chatReasoningEffort: chatReasoningEffort);
        return (new AiIntentService(Orbit.Infrastructure.Tests.AiBudgetTestDouble.Wrap(client), usage,
            NullLogger<AiIntentService>.Instance), client, handler);
    }

    private static string FirstRoundWithOneRead()
    {
        var response = ResponsesTestData.Response(inputTokens: 11, outputTokens: 7);
        var call = ResponseItem.CreateFunctionCallItem("call_1", "query_habits", BinaryData.FromString("{}"));
        call.Id = "fc_1";
        response.OutputItems.Add(call);
        return ModelReaderWriter.Write(response).ToString();
    }

    private static (string? CacheKey, string? Effort) KeyAndEffort(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return (root.TryGetProperty("prompt_cache_key", out var key) ? key.GetString() : null,
            root.TryGetProperty("reasoning", out var reasoning) ? reasoning.GetProperty("effort").GetString() : null);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    public async Task ChangePlanChat_SendsThePlanCacheKeyAndTheConfiguredEffort_InEveryRound(string effort)
    {
        var (service, _, handler) = BuildChangePlanService(effort, FirstRoundWithOneRead(), ResponsesTestData.Text("Done"));
        var declaration = new { name = "query_habits", description = "Read habits", parameters = new { type = "object", properties = new { } } };

        var first = await service.SendWithToolsAsync(new AiToolRequest("rename my water habits", "system", [declaration], Guid.NewGuid())
        {
            ChangePlan = true
        });
        first.IsSuccess.Should().BeTrue();
        var second = await service.ContinueWithToolResultsAsync(first.Value.ConversationContext!,
            [new AiToolCallResult("query_habits", "call_1", true, null, null, null)]);
        second.IsSuccess.Should().BeTrue();

        handler.RequestBodies.Should().HaveCount(2);
        handler.RequestBodies.Select(KeyAndEffort).Should().AllBeEquivalentTo(("orbit:gpt-6-luna:plan", effort));
    }

    [Fact]
    public async Task OldPathChat_SendsTheModelCacheKeyAndLowEffort_WhatEverTheSetting()
    {
        var (service, _, handler) = BuildChangePlanService("medium", FirstRoundWithOneRead(), ResponsesTestData.Text("Done"));
        var declaration = new { name = "query_habits", description = "Read habits", parameters = new { type = "object", properties = new { } } };

        var first = await service.SendWithToolsAsync(new AiToolRequest("rename my water habits", "system", [declaration], Guid.NewGuid()));
        await service.ContinueWithToolResultsAsync(first.Value.ConversationContext!,
            [new AiToolCallResult("query_habits", "call_1", true, null, null, null)]);

        handler.RequestBodies.Select(KeyAndEffort).Should().AllBeEquivalentTo(("orbit:gpt-6-luna", "low"));
    }

    [Fact]
    public async Task ChangePlanFlagOnAnotherPurpose_KeepsTheModelCacheKeyAndLowEffort()
    {
        var (service, _, handler) = BuildChangePlanService("medium", ResponsesTestData.Text("{}"));

        await service.SendWithToolsAsync(new AiToolRequest("message", "select one offer", [], Guid.NewGuid())
        {
            Purpose = "offer_selection",
            ChangePlan = true
        });

        KeyAndEffort(handler.RequestBodies.Single()).Should().Be(("orbit:gpt-6-luna", "low"));
    }

    [Fact]
    public async Task SummaryCall_KeepsTheModelCacheKeyAndLowEffort_WhenTheChatEffortIsMedium()
    {
        var (_, client, handler) = BuildChangePlanService("medium", ResponsesTestData.Text("A short recap"));
        using var call = AiCallContext.Begin(Guid.NewGuid());

        await client.CompleteTextAsync("system", "summarize my day", purpose: "daily_summary");

        KeyAndEffort(handler.RequestBodies.Single()).Should().Be(("orbit:gpt-6-luna", "low"));
    }

    [Theory]
    [InlineData(null, "low")]
    [InlineData("", "low")]
    [InlineData(" Medium ", "medium")]
    [InlineData("low", "low")]
    public void ChatReasoningEffort_TakesTheProviderEffortName(string? configured, string expected)
    {
        AiCompletionClient.ResolveChatReasoningEffort(configured).ToString().Should().Be(expected);
    }
}
