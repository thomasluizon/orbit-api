#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.AI;

public class AiInputBudgetTests(ITestOutputHelper output)
{
    private static IReadOnlyList<IAiTool> RegisteredTools() => new AiToolRegistry(typeof(IAiTool).Assembly.GetTypes()
        .Where(type => type.IsClass && !type.IsAbstract && typeof(IAiTool).IsAssignableFrom(type))
        .Select(type => (IAiTool)RuntimeHelpers.GetUninitializedObject(type))).GetAll();

    private static object Declare(IAiTool tool) => new { name = tool.Name, description = tool.Description, parameters = tool.GetParameterSchema() };

    [Fact]
    public void KeywordlessTurn_DeclaresFewerThanTwentyToolsIncludingLoader()
    {
        var tools = RegisteredTools();
        var active = ChatToolGroups.ResolveActiveToolNames(tools.Select(tool => tool.Name), "please help me");
        active.Order().Should().BeEquivalentTo(new[]
        {
            "ask", "create_goal", "create_habit", "delete_habit", "describe_feature", "get_gamification_overview",
            "get_profile", "log_habit", "move_habit", "query_goals", "query_habits", "skip_habit",
            "update_goal", "update_goal_progress", "update_habit"
        });
        (active.Count + 1).Should().BeLessThan(20);
    }

    [Fact]
    public async Task Requests_TwoUsersAndTwoTurns_ShareIdenticalToolsAndStaticMessage()
    {
        var handler = new SequenceHandler(ResponsesTestData.Text("done"));
        var service = BuildService(handler);
        var builder = new SystemPromptBuilder();
        var catalog = new AgentCatalogService();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var tools = RegisteredTools().Where(tool => tool.Name != "ask").ToList();
        var active = ChatToolGroups.ResolveActiveToolNames(tools.Select(tool => tool.Name), "log my run");
        for (var index = 0; index < 3; index++)
        {
            var prompt = new PromptBuildRequest([], UserToday: new DateOnly(2026, 1, 1).AddDays(index));
            var result = await service.SendWithToolsAsync(new AiToolRequest($"log my run {index}",
                builder.BuildStatic(prompt) + catalog.BuildStaticSupplement(),
                tools.Where(tool => active.Contains(tool.Name)).OrderBy(tool => tool.Name).Select(Declare).ToList(),
                index == 2 ? userB : userA,
                History: [new("user", $"history-{index}")])
            {
                DynamicContext = builder.BuildDynamic(prompt) + $"private-facts-{index}",
                AvailableToolDeclarations = tools.OrderBy(tool => tool.Name).Select(Declare).ToList()
            });
            result.IsSuccess.Should().BeTrue();
        }

        var documents = handler.Bodies.Select(body => JsonDocument.Parse(body)).ToList();
        try
        {
            var prefixes = documents.Select(document => document.RootElement.GetProperty("tools").GetRawText()
                + document.RootElement.GetProperty("input")[0].GetRawText()).ToList();
            Encoding.UTF8.GetBytes(prefixes[0]).Should().Equal(Encoding.UTF8.GetBytes(prefixes[1]));
            Encoding.UTF8.GetBytes(prefixes[0]).Should().Equal(Encoding.UTF8.GetBytes(prefixes[2]));
            documents.Select(document => document.RootElement.GetProperty("input")[1].GetRawText()).Distinct().Should().HaveCount(3);
            documents[0].RootElement.GetProperty("input")[2].GetRawText().Should().Contain("history-0");
        }
        finally { documents.ForEach(document => document.Dispose()); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolMissingFromKeywords_CanBeLoadedAndCalled(bool mixedWithCoreCall)
    {
        var first = ResponsesTestData.Response();
        first.OutputItems.Add(ResponseItem.CreateFunctionCallItem("load_1", "load_tools", BinaryData.FromString("{\"tool_names\":[\"duplicate_habit\"]}")));
        if (mixedWithCoreCall)
            first.OutputItems.Add(ResponseItem.CreateFunctionCallItem("query_1", "query_habits", BinaryData.FromString("{}")));
        var second = ResponsesTestData.Response();
        second.OutputItems.Add(ResponseItem.CreateFunctionCallItem("duplicate_1", "duplicate_habit", BinaryData.FromString("{}")));
        var handler = new SequenceHandler(ModelReaderWriter.Write(first).ToString(), ModelReaderWriter.Write(second).ToString());
        var service = BuildService(handler);
        var tools = RegisteredTools().Where(tool => tool.Name != "ask").ToList();
        var result = await service.SendWithToolsAsync(new AiToolRequest("make another one just like it", "static",
            tools.Where(tool => tool.Name == "query_habits").Select(Declare).ToList())
        {
            AvailableToolDeclarations = tools.Select(Declare).ToList()
        });
        if (mixedWithCoreCall)
        {
            result.Value.ToolCalls!.Should().ContainSingle(call => call.Name == "query_habits");
            result = await service.ContinueWithToolResultsAsync(result.Value.ConversationContext!,
                [new("query_habits", "query_1", true, null, null, null)]);
        }
        result.IsSuccess.Should().BeTrue();
        result.Value.ToolCalls!.Should().ContainSingle(call => call.Name == "duplicate_habit");
        using var initial = JsonDocument.Parse(handler.Bodies[0]);
        using var loaded = JsonDocument.Parse(handler.Bodies[1]);
        initial.RootElement.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString())
            .Should().NotContain("duplicate_habit");
        loaded.RootElement.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString())
            .Should().Contain("duplicate_habit").And.NotContain("ask");
    }

    [Fact]
    public async Task FixedTurns_SerializedRequestTokenEstimate_DropsAtLeastThirtyPercent()
    {
        string[] turns = ["obrigado", "log my run today", "create a habit every wednesday and sunday",
            "criar um hábito toda quarta e domingo", "log my run and show my progress", "bulk log all habits today"];
        var tools = RegisteredTools().Where(tool => tool.Name != "ask").ToList();
        var promptRequest = new PromptBuildRequest([], UserToday: new DateOnly(2026, 1, 1));
        var builder = new SystemPromptBuilder();
        var staticPrompt = builder.BuildStatic(promptRequest) + new AgentCatalogService().BuildStaticSupplement();
        var dynamicPrompt = builder.BuildDynamic(promptRequest);
        long beforeTotal = 0, afterTotal = 0;
        foreach (var turn in turns)
        {
            var fullHandler = new SequenceHandler(ResponsesTestData.Text("done"));
            var gatedHandler = new SequenceHandler(ResponsesTestData.Text("done"));
            var active = ChatToolGroups.ResolveActiveToolNames(tools.Select(tool => tool.Name), turn);
            var full = tools.OrderBy(tool => tool.Name).Select(Declare).ToList();
            var selected = ChatIntentRouter.IsNoToolTurn(turn) ? [] : tools.Where(tool => active.Contains(tool.Name)).OrderBy(tool => tool.Name).Select(Declare).ToList();
            await BuildService(fullHandler).SendWithToolsAsync(new AiToolRequest(turn, staticPrompt + dynamicPrompt, full));
            await BuildService(gatedHandler).SendWithToolsAsync(new AiToolRequest(turn, staticPrompt, selected)
            {
                DynamicContext = dynamicPrompt,
                AvailableToolDeclarations = selected.Count == 0 ? [] : full
            });
            var before = EstimateTokens(fullHandler.Bodies.Single());
            var after = EstimateTokens(gatedHandler.Bodies.Single());
            beforeTotal += before;
            afterTotal += after;
            output.WriteLine($"{turn}: {before} -> {after} estimated tokens ({selected.Count} selected tools)");
        }
        afterTotal.Should().BeLessThan((long)(beforeTotal * 0.70));
    }

    private static long EstimateTokens(string request) => (Encoding.UTF8.GetByteCount(request) + 3L) / 4;

    private static AiIntentService BuildService(HttpMessageHandler handler)
    {
        var responses = new ResponsesClient(new ApiKeyCredential("test"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        });
        var usage = Substitute.For<IAiUsageRecorder>();
        return new AiIntentService(new AiCompletionClient(responses, NullLogger<AiCompletionClient>.Instance, usage),
            usage, NullLogger<AiIntentService>.Instance);
    }

    private sealed class SequenceHandler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[Math.Min(Bodies.Count - 1, responses.Length - 1)], Encoding.UTF8, "application/json")
            };
        }
    }
}
