#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Linq.Expressions;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed partial class OfferDeadlineControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EquivalentGoalRequest_ReadOfferUsesNoSelectorAndPreservesCancellation(bool continuation, bool callerCancelled)
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath("OfferDeadlineRequest.json")))!.AsObject();
        using var transport = new DeadlineTransport(continuation, stallMain: callerCancelled);
        using var http = new HttpClient(transport);
        using var caller = new CancellationTokenSource();
        var (controller, goal, offers, mediator, messages) = CreateController(http, caller.Token);
        var pending = controller.ProcessChat(input["message"]!.GetValue<string>(), input["history"]!.GetValue<string>(),
            null, caller.Token, input["clientContext"]!.ToJsonString(), evalTrace: "1");
        if (callerCancelled)
        {
            await transport.StalledRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await caller.CancelAsync();
            Func<Task> completion = () => pending;
            await completion.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            var result = (await pending).Should().BeOfType<OkObjectResult>().Subject;
            result.StatusCode.Should().Be(StatusCodes.Status200OK);
            var reply = result.Value.Should().BeOfType<ChatResponse>().Subject;
            var card = reply.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Subject.Block
                .Should().BeOfType<GoalListCard>().Subject;
            card.Items.Should().ContainSingle(item => item.Id == goal.Id.ToString() && item.Title == goal.Title);
            card.Read!.NextAction.Should().BeNull();
            reply.PendingOperations.Should().BeNullOrEmpty();
            reply.FollowUps.Should().BeNullOrEmpty();
            reply.MessageAllowanceConsumed.Should().BeTrue();
            reply.EvalTrace!.ToolCalls.Should().ContainSingle(call => call.Name == "query_goals");
        }

        transport.Cancelled.Should().Be(callerCancelled);
        transport.Requests.Should().HaveCount(callerCancelled ? 1 : continuation ? 2 : 1);
        transport.Requests.Should().OnlyContain(body => !body.Contains(ProactiveOfferPlanner.SelectionInstruction, StringComparison.Ordinal));
        transport.Requests[0].Should().Contain("Run five kilometers");
        if (callerCancelled)
            messages.Should().Contain("AI completion failed at completion with cancelled");
        else
            messages.Should().NotContain("AI completion failed at completion with cancelled");
        await offers.DidNotReceiveWithAnyArgs().IssueAsync(default, default, default!, default);
        await mediator.Received(1).Send(Arg.Is<ProcessUserChatCommand>(command =>
            command.Message == input["message"]!.GetValue<string>() && command.History!.Count == 0
            && command.StreamSink == null && command.TurnId == null && command.EvalTraceRequested
            && command.ClientContext!.ConversationId == Guid.Parse("422cab4f-4463-5976-8b8d-d5202357aaf1")
            && command.ClientContext.SupportsProactiveOffers == true
            && command.ClientContext.SupportsPendingOperationPlacement == true
            && command.ClientContext.SupportsMessageAllowance == true
            && command.ClientContext.SupportsFollowUpContext == true), caller.Token);
    }

    private static (ChatController Controller, Goal Goal, IConversationOfferStore Offers, IMediator Mediator, List<string> Messages)
        CreateController(HttpClient http, CancellationToken caller, OrbitDbContext? db = null)
    {
        var user = User.Create("Reader", "offer-deadline@example.test").Value;
        user.SetLanguage("en");
        user.SetTimeZone("UTC");
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var goal = Goal.Create(user.Id, "Run five kilometers", 5, "km").Value;
        var goals = Repository(goal);
        var habits = Repository<Habit>();
        var alerts = Enumerable.Range(1, 3)
            .Select(index => Notification.Create(user.Id, $"Alert {index}", "Unread alert")).ToArray();
        var notifications = Repository(alerts);
        notifications.ProjectAsync(Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<Func<IQueryable<Notification>, IQueryable<NotificationRevision>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<IQueryable<Notification>, IQueryable<NotificationRevision>>>()(
                alerts.Where(call.Arg<Expression<Func<Notification, bool>>>().Compile()).AsQueryable()).ToArray());
        var mediator = Substitute.For<IMediator>();
        var notificationQuery = new GetNotificationsQueryHandler(notifications);
        mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => notificationQuery.Handle(call.Arg<GetNotificationsQuery>(), call.Arg<CancellationToken>()));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 11, 1));
        dates.GetUserTimeZoneAsync(user.Id, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var validationServices = new ServiceCollection().AddSingleton(dates).BuildServiceProvider();
                return await new CheckChatCommandQueryHandler(validationServices)
                    .Handle(call.Arg<CheckChatCommandQuery>(), call.Arg<CancellationToken>());
            });
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(user.Id, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        IAiTool[] tools = [new QueryGoalsTool(goals, dates, progress), new GetNotificationsTool(mediator)];
        if (db is not null) tools = [.. tools, new CreateGoalTool(mediator)];
        var registry = new AiToolRegistry(tools);
        var catalog = new AgentCatalogService(tools);
        var tokens = new ReadQueryTokenService(Options.Create(new JwtSettings
        {
            SecretKey = "offer-deadline-test-signing-key-32-bytes",
            Issuer = "test",
            Audience = "test"
        }));
        var blocks = new ReadBlockFactory(dates, tokens, users);
        IAgentPolicyEvaluator policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call => new AgentPolicyDecision(
            AgentPolicyDecisionStatus.Allowed, catalog.GetCapability(call.Arg<AgentPolicyEvaluationContext>().CapabilityId)!));
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        IPendingAgentOperationStore pending = Substitute.For<IPendingAgentOperationStore>();
        var previewer = new PendingOperationChangePreviewer(habits, goals, Repository<Tag>(), dates, registry);
        if (db is not null)
        {
            db.Users.Add(user);
            db.Goals.Add(goal);
            db.SaveChanges();
            pending = new PendingAgentOperationStore(db, Options.Create(new AgentPlatformSettings()));
            policy = new AgentPolicyEvaluator(db, catalog, pending, Substitute.For<IAgentStepUpAuthorizationBridge>(),
                Options.Create(new AgentPlatformSettings()), Substitute.For<IFeatureFlagService>());
        }
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var executor = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            ownership, bridge, registry,
            previewer, pending, work, NullLogger<AgentOperationExecutor>.Instance, blocks);
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IAgentOperationExecutor)).Returns(executor);
        var clarifications = Substitute.For<IPendingClarificationStore>();
        services.GetService(typeof(IPendingClarificationStore)).Returns(clarifications);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(services);
        var scopes = Substitute.For<IServiceScopeFactory>();
        scopes.CreateScope().Returns(scope);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraBlockFirst, FeatureFlagKeys.AstraEvalTrace]);
        var payGate = Substitute.For<IPayGateService>();
        payGate.TryConsumeAiMessage(user.Id, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var messages = new List<string>();
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(Options.Create(new AiSettings { ApiKey = "test-key", BaseUrl = "https://orbit.test/v1" }),
            new CaptureLogger<AiCompletionClient>(messages), usage, new HttpClientPipelineTransport(http));
        var estimates = Substitute.For<IAiRequestCostEstimator>();
        estimates.EstimateMicroDollarsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1000L);
        var budget = new BudgetedAiCompletionClient(client, Substitute.For<IAiDailyBudget>(), new HttpContextAccessor(),
            new CaptureLogger<BudgetedAiCompletionClient>(messages), estimates);
        var intent = new AiIntentService(budget, usage, new CaptureLogger<AiIntentService>(messages));
        var offers = Substitute.For<IConversationOfferStore>();
        offers.DismissedAsync(user.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new HashSet<string>());
        var handler = new ProcessUserChatCommandHandler(
            new(habits, goals, users, Repository<Tag>(), Repository<ChecklistTemplate>(), flags),
            new(intent, registry, Substitute.For<ISystemPromptBuilder>(), catalog),
            new(dates, Substitute.For<IUserStreakService>(), payGate, work, scopes, executor,
                clarifications, progress, Substitute.For<IGamificationService>(), mediator,
                Substitute.For<IProductAnalytics>(), Substitute.For<IHabitScheduleLogReader>(),
                Substitute.For<IIssuedAstraBlockStore>(), pending),
            new CaptureLogger<ProcessUserChatCommandHandler>(messages), Substitute.For<IAppConfigService>(),
            ModerationTestDouble.Clean(), offerStore: offers);
        mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<ProcessUserChatCommand>(), call.Arg<CancellationToken>()));
        var context = new DefaultHttpContext
        {
            RequestAborted = caller,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
        };
        context.Request.Method = "POST";
        context.Request.Path = "/api/chat";
        context.Request.ContentType = "multipart/form-data; boundary=offer-deadline";
        context.Request.Headers["X-Orbit-Eval-Trace"] = "1";
        context.Request.Headers["X-Orbit-Request-Id"] = context.TraceIdentifier;
        var controller = new ChatController(mediator, Substitute.For<IImageValidationService>(), NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        return (controller, goal, offers, mediator, messages);
    }

    private static IGenericRepository<T> Repository<T>(params T[] rows) where T : Entity
    {
        var repository = Substitute.For<IGenericRepository<T>>();
        repository.FindAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => rows.Where(call.Arg<Expression<Func<T, bool>>>().Compile()).ToArray());
        repository.FindAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<Func<IQueryable<T>, IQueryable<T>>>(), Arg.Any<CancellationToken>())
            .Returns(call => rows.Where(call.Arg<Expression<Func<T, bool>>>().Compile()).ToArray());
        repository.CountAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => rows.Count(call.Arg<Expression<Func<T, bool>>>().Compile()));
        return repository;
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Chat", name);

    private sealed class CaptureLogger<T>(List<string> messages) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
    }

    private sealed class DeadlineTransport(bool continuation, bool stallMain = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool Cancelled { get; private set; }
        public TaskCompletionSource StalledRequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (stallMain || Requests.Count > (continuation ? 2 : 1))
            {
                StalledRequestEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Cancelled = true;
                    throw;
                }
            }
            var response = ResponsesTestData.Response(Requests.Count == 2 ? "Ready." : null);
            if (Requests.Count == 1)
            {
                var capture = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath("GoalRead.json"), cancellationToken))!;
                var call = capture["toolCalls"]![0]!;
                var arguments = call["arguments"]!.DeepClone();
                arguments[TerminalReadDirective.CompleteArgument] = !continuation;
                response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("read", call["name"]!.GetValue<string>(),
                    BinaryData.FromString(arguments.ToJsonString())));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ModelReaderWriter.Write(response).ToString(), Encoding.UTF8, "application/json")
            };
        }
    }
}
