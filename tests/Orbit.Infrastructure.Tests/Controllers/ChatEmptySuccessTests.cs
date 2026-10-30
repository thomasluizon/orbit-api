#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.AI;
using Orbit.Application.Chat.Models;
using System.Linq.Expressions;
using System.Text.Json;
using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ChatEmptySuccessTests
{
    public static TheoryData<bool, bool, string, string, bool, bool> EmptyReplies => new()
    {
        { false, false, "en", "[[orbit:ranking]]", false, false },
        { true, false, "en", "[[orbit:ranking]]", false, false },
        { false, true, "pt-BR", "[[orbit:ranking]]", false, false },
        { true, true, "pt-BR", "[[orbit:ranking]]", false, false },
        { false, true, "en", "# Ranking", false, false },
        { true, true, "en", "# Ranking", false, false },
        { false, false, "pt-BR", "You completed 99 habits.", true, false },
        { true, false, "pt-BR", "You completed 99 habits.", true, false },
        { false, false, "en", "[[orbit:ranking]]", false, true },
        { true, false, "en", "[[orbit:ranking]]", false, true },
        { false, true, "en", "[[orbit:ranking]]", false, true },
        { true, true, "en", "[[orbit:ranking]]", false, true },
        { false, false, "pt-BR", "[[orbit:ranking]]", false, true },
        { true, false, "pt-BR", "[[orbit:ranking]]", false, true },
        { false, true, "pt-BR", "[[orbit:ranking]]", false, true },
        { true, true, "pt-BR", "[[orbit:ranking]]", false, true }
    };

    [Theory]
    [MemberData(nameof(EmptyReplies))]
    public async Task Endpoints_EmptyAfterCompositionOrGrounding_ReturnLocalizedAnswer(
        bool stream, bool composition, string language, string text, bool enforceGrounding, bool successfulRead)
    {
        var logger = Substitute.For<ILogger<ProcessUserChatCommandHandler>>();
        var reads = new List<AgentExecuteOperationResponse>();
        var (controller, body) = CreateController(text, composition, language, enforceGrounding, stream, logger, successfulRead, reads);
        using var responseBody = body;
        var client = JsonSerializer.Serialize(new AgentClientContext(SupportsParts: true, PartKinds: ["habitRanking"]));
        if (stream)
        {
            await controller.ProcessChatStream("Which habits did I complete most in the last 3 weeks?", null, null,
                default, clientContext: client);
            body.Position = 0;
            var events = (await new StreamReader(body).ReadToEndAsync()).Split('\n')
                .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line[6..])).ToArray();
            events.Should().NotContain(item => item.GetProperty("type").GetString() == "error");
            events.Should().Contain(item => item.GetProperty("type").GetString() == "final");
            Encoding.UTF8.GetString(body.ToArray()).Should().Contain(language == "pt-BR" ? "resposta" : "answer");
        }
        else
        {
            var result = await controller.ProcessChat("Which habits did I complete most in the last 3 weeks?", null, null,
                default, clientContext: client);
            var success = result.Should().BeOfType<OkObjectResult>().Subject;
            var json = JsonSerializer.SerializeToElement(success.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            json.GetProperty("aiMessage").GetString().Should().Be(language == "pt-BR"
                ? "Não consegui preparar uma resposta. Tente novamente."
                : "I couldn't prepare an answer. Please try again.");
        }
        var warnings = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log"
            && call.GetArguments()[0] is LogLevel.Warning).Select(call => call.GetArguments()[2]).ToArray();
        var warning = warnings.OfType<IReadOnlyList<KeyValuePair<string, object>>>()
            .Should().ContainSingle(state => state.Any(pair => pair.Key == "RoundIndex")).Which;
        warning.Single(pair => pair.Key == "RoundIndex").Value.Should().Be(successfulRead ? 2 : 1);
        warning.Single(pair => pair.Key == "OutputKinds").Value.Should().NotBeNull();
        warning.ToString().Should().NotContain(text);
        if (successfulRead)
        {
            var read = reads.Should().ContainSingle().Which.Operation;
            read.OperationId.Should().Be("describe_feature");
            read.Status.Should().Be(AgentOperationStatus.Succeeded);
            read.Payload.Should().NotBeNull();
            read.ReadBlock.Should().BeNull();
        }
    }

    private static (ChatController Controller, MemoryStream Body) CreateController(string text, bool composition,
        string language, bool enforceGrounding, bool stream, ILogger<ProcessUserChatCommandHandler> logger,
        bool successfulRead = false, List<AgentExecuteOperationResponse>? reads = null)
    {
        var user = User.Create("Person", "empty@example.com").Value;
        user.SetLanguage(language);
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(composition ? [FeatureFlagKeys.AstraBlockFirst] : []);
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Habit>());
        var goals = Substitute.For<IGenericRepository<Goal>>();
        goals.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Goal>());
        var dateService = Substitute.For<IUserDateService>();
        dateService.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 1, 1));
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(user.Id, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        var payGate = Substitute.For<IPayGateService>();
        payGate.TryConsumeAiMessage(user.Id, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var responses = new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(new ReplyHandler(text, stream && !composition && !enforceGrounding, successfulRead)))
        });
        var usage = Substitute.For<IAiUsageRecorder>();
        var intent = new AiIntentService(AiBudgetTestDouble.Wrap(new AiCompletionClient(responses,
            NullLogger<AiCompletionClient>.Instance, usage)), usage, NullLogger<AiIntentService>.Instance);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.AstraGroundingEnforce, false, Arg.Any<CancellationToken>()).Returns(enforceGrounding);
        var recorder = Substitute.For<IConversationTurnRecorder>();
        var mediator = Substitute.For<IMediator>();
        IAiTool[] tools = successfulRead ? [new DescribeFeatureTool(new FeatureExplanationService(), new AppSurfaceCatalog())] : [];
        var catalog = new AgentCatalogService(tools);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call =>
            new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed,
                catalog.GetCapability(call.Arg<AgentPolicyEvaluationContext>().CapabilityId)!));
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var executor = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            ownership, bridge, new(tools),
            Substitute.For<IPendingOperationChangePreviewer>(), Substitute.For<IPendingAgentOperationStore>(),
            work, NullLogger<AgentOperationExecutor>.Instance);
        var operationPort = Substitute.For<IAgentOperationExecutor>();
        operationPort.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var result = await executor.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>());
            reads?.Add(result);
            return result;
        });
        var clarifications = Substitute.For<IPendingClarificationStore>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scopeProvider.GetService(typeof(IAgentOperationExecutor)).Returns(operationPort);
        scopeProvider.GetService(typeof(IPendingClarificationStore)).Returns(clarifications);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopeProvider);
        var scopes = Substitute.For<IServiceScopeFactory>();
        scopes.CreateScope().Returns(scope);
        var handler = new ProcessUserChatCommandHandler(
            new ChatDataDependencies(habits, goals, users, Substitute.For<IGenericRepository<Tag>>(),
                Substitute.For<IGenericRepository<ChecklistTemplate>>(), flags),
            new ChatAiDependencies(intent, new AiToolRegistry(tools), Substitute.For<ISystemPromptBuilder>(), catalog),
            new ChatExecutionDependencies(dateService, Substitute.For<IUserStreakService>(), payGate, work,
                scopes, operationPort, clarifications,
                progress, Substitute.For<IGamificationService>(), mediator, Substitute.For<IProductAnalytics>(),
                Substitute.For<IHabitScheduleLogReader>(), Substitute.For<IIssuedAstraBlockStore>(), Substitute.For<IPendingAgentOperationStore>()),
            logger, config, ModerationTestDouble.Clean(), recorder);
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<ProcessUserChatCommand>(), call.Arg<CancellationToken>()));
        var body = new MemoryStream();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
        };
        http.Response.Body = body;
        var controller = new ChatController(mediator, Substitute.For<IImageValidationService>(), NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        return (controller, body);
    }

    private sealed class ReplyHandler(string text, bool stream, bool successfulRead) : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var initialRead = successfulRead && _requests++ == 0;
            var response = ResponsesTestData.Response(initialRead ? null : text);
            if (initialRead)
                response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("feature", "describe_feature",
                    BinaryData.FromString("{\"feature_key\":\"streaks\"}")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream ? ResponsesTestData.Stream(response, initialRead ? [] : [text])
                    : ModelReaderWriter.Write(response).ToString(), Encoding.UTF8,
                    stream ? "text/event-stream" : "application/json")
            });
        }
    }
}
