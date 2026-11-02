#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false, false)]
    [InlineData("pt-BR", false, false)]
    [InlineData("en", true, false)]
    [InlineData("pt-BR", true, false)]
    [InlineData("en", false, true)]
    [InlineData("pt-BR", false, true)]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", true, true)]
    public async Task ReadOffer_RealCompletionChainPreservesReadWithoutASelector(string locale, bool continuation, bool providerFailure)
    {
        using var transport = new OfferDeadlineHandler(continuation, providerFailure);
        using var http = new HttpClient(transport);
        var (handler, goal) = CreateOfferDeadlineReplay(http);
        using var caller = new CancellationTokenSource();

        var result = await handler.Handle(new(UserId, "Show my goals.", ClientContext: OfferClient(locale)), caller.Token);

        transport.StalledRequestEntered.Task.IsCompletedSuccessfully.Should().BeFalse();
        transport.Cancelled.Should().BeFalse();
        transport.RequestCount.Should().Be(continuation ? 2 : 1);
        caller.IsCancellationRequested.Should().BeFalse();
        result.IsSuccess.Should().BeTrue(result.ErrorCode);
        var card = result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Subject.Block
            .Should().BeOfType<GoalListCard>().Subject;
        card.Items.Should().ContainSingle(item => item.Id == goal.Id.ToString());
        card.Read!.NextAction.Should().BeNull();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _offers.DidNotReceiveWithAnyArgs().IssueAsync(default, default, default!, default);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task OfferDeadline_CallerCancellationPropagatesThroughRealCompletionChain(string locale)
    {
        using var transport = new OfferDeadlineHandler(false, false, stallMain: true);
        using var http = new HttpClient(transport);
        var (handler, _) = CreateOfferDeadlineReplay(http);
        using var caller = new CancellationTokenSource();
        var pending = handler.Handle(new(UserId, "Show my goals.", ClientContext: OfferClient(locale)), caller.Token);
        await transport.StalledRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await caller.CancelAsync();

        Func<Task> completion = () => pending;
        await completion.Should().ThrowAsync<OperationCanceledException>();
        transport.Cancelled.Should().BeTrue();
        transport.RequestCount.Should().Be(1);
        await _offers.DidNotReceiveWithAnyArgs().IssueAsync(default, default, default!, default);
    }

    private (ProcessUserChatCommandHandler Handler, Goal Goal) CreateOfferDeadlineReplay(HttpClient http)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var goal = Goal.Create(UserId, "Run five kilometers", 5, "km").Value;
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal]);
        var query = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var registry = new AiToolRegistry([query]);
        UseProductionReadExecutor(query);
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(Options.Create(new AiSettings
        {
            ApiKey = "test-key",
            BaseUrl = "https://orbit.test/v1"
        }), NullLogger<AiCompletionClient>.Instance, usage, new HttpClientPipelineTransport(http));
        var estimates = Substitute.For<IAiRequestCostEstimator>();
        estimates.EstimateMicroDollarsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1000L);
        var budget = new BudgetedAiCompletionClient(client, Substitute.For<IAiDailyBudget>(),
            new HttpContextAccessor(), NullLogger<BudgetedAiCompletionClient>.Instance, estimates);
        var intent = new AiIntentService(budget, usage, NullLogger<AiIntentService>.Instance);
        return (new ProcessUserChatCommandHandler(
            new(_habitRepo, _goalRepo, _userRepo, _tagRepo, _checklistTemplateRepo, _featureFlagService),
            new(intent, registry, _promptBuilder, _catalogService),
            new(_userDateService, _userStreakService, _payGate, _unitOfWork, _scopeFactory, _operationExecutor,
                _pendingClarificationStore, _goalProgressReadSyncer, _gamificationService, _mediator, _productAnalytics,
                _scheduleLogReader, _blockStore, _pendingOperationStore),
            _logger, _appConfig, _moderation, _conversationRecorder, offerStore: _offers), goal);
    }

    private sealed class OfferDeadlineHandler(bool continuation, bool providerFailure, bool stallMain = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public bool Cancelled { get; private set; }
        public TaskCompletionSource StalledRequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var body = JsonDocument.Parse(json);
            if (stallMain || !body.RootElement.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0)
            {
                StalledRequestEntered.TrySetResult();
                if (providerFailure)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Cancelled = true;
                    throw;
                }
            }
            using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "ProactiveOffers", "offers-goal-gap-en-r2.json"), cancellationToken));
            var call = capture.RootElement.GetProperty("toolCalls")[0];
            var response = new ResponseResult
            {
                Id = "resp_offer_deadline",
                Model = "gpt-6-luna",
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1700000000),
                Status = ResponseStatus.Completed,
                Usage = new ResponseTokenUsage { InputTokenCount = 1, OutputTokenCount = 2, TotalTokenCount = 3 }
            };
            if (RequestCount == 1)
            {
                var arguments = JsonNode.Parse(call.GetProperty("arguments").GetRawText())!.AsObject();
                arguments[TerminalReadDirective.CompleteArgument] = !continuation;
                response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("read", call.GetProperty("name").GetString()!,
                    BinaryData.FromString(arguments.ToJsonString())));
            }
            else
                response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Ready."));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ModelReaderWriter.Write(response).ToString(), Encoding.UTF8, "application/json")
            };
        }
    }
}
