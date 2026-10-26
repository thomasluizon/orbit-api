using System.ClientModel.Primitives;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "en", true)]
    [InlineData("pt-BR", "pt-BR", true)]
    [InlineData("en", "original-en", false)]
    public async Task TerminalRead_ProviderToolOnlyResponseUsesStructuredCompletion(string language, string captureName, bool complete)
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraBlockFirst, FeatureFlagKeys.AstraEvalTrace]);
        var ranking = await ProduceRankingAsync();
        _mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(ranking));
        var tool = new GetHabitRankingTool(_mediator);
        using var transport = new CapturedReadResponseHandler(captureName);
        using var http = new HttpClient(transport);
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(Options.Create(new AiSettings { ApiKey = "test-key", MaxRetries = 0 }),
            NullLogger<AiCompletionClient>.Instance, usage, new HttpClientPipelineTransport(http));
        var budgeted = new BudgetedAiCompletionClient(client, Substitute.For<IAiDailyBudget>(), new HttpContextAccessor(),
            NullLogger<BudgetedAiCompletionClient>.Instance, Substitute.For<IAiRequestCostEstimator>());
        var adapter = new AiIntentService(budgeted, usage, NullLogger<AiIntentService>.Instance);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(call => adapter.SendWithToolsAsync(call.Arg<AiToolRequest>(),
                call.Arg<Func<AiStreamEvent, Task>?>(), call.Arg<CancellationToken>()));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(call => adapter.ContinueWithToolResultsAsync(
                call.Arg<AiConversationContext>(), call.Arg<IReadOnlyList<AiToolCallResult>>(),
                call.Arg<Func<AiStreamEvent, Task>?>(), call.Arg<CancellationToken>()));
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);

        var result = await handler.Handle(new(UserId,
            language == "en" ? "Show my habit ranking for the last six months." : "Mostre o ranking dos meus hábitos nos últimos seis meses.",
            EvalTraceRequested: true, ClientContext: new(SupportsParts: true, PartKinds: ["habitRanking"])), default);

        result.IsSuccess.Should().BeTrue();
        transport.Requests.Should().Be(complete ? 1 : 2);
        var schema = transport.RequestBody.GetProperty("tools").EnumerateArray().Single().GetProperty("parameters");
        schema.GetProperty("properties").GetProperty("orbit_read_complete").GetProperty("type").GetString().Should().Be("boolean");
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).Should().Contain("orbit_read_complete");
        JsonSerializer.Serialize(tool.GetParameterSchema()).Should().NotContain("orbit_read_complete");
        result.Value.EvalTrace!.Timing!.Phases.Count(phase => phase.Name == "model_call").Should().Be(complete ? 1 : 2);
        result.Value.Parts!.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<HabitRankingCard>().Which.Ranking.Should().Be(ranking);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _aiIntentService.Received(complete ? 0 : 1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            !request.Arguments.EnumerateObject().Any(property => property.Name == "orbit_read_complete")), Arg.Any<CancellationToken>());
    }

    private sealed class CapturedReadResponseHandler(string captureName) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public JsonElement RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Requests > 1)
                throw new InvalidOperationException("A terminal read must not request a continuation.");
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestBody = sent.RootElement.Clone();
            await using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TerminalReads", captureName + ".json.gz"));
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var capture = await JsonDocument.ParseAsync(gzip, cancellationToken: cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(capture.RootElement.GetProperty("response").GetRawText(), Encoding.UTF8, "application/json")
            };
        }
    }
}
