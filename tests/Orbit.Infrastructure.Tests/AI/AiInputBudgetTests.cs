using Orbit.Domain.Enums;
#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Data.Common;
using System.Net;
using System.Linq.Expressions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Entities;
using Orbit.Domain.ValueObjects;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiInputBudgetTests(ITestOutputHelper output)
{
    internal static IReadOnlyList<IAiTool> RegisteredTools() => new AiToolRegistry(typeof(IAiTool).Assembly.GetTypes()
        .Where(type => type.IsClass && !type.IsAbstract && typeof(IAiTool).IsAssignableFrom(type))
        .Select(CreateRegisteredTool)).GetAll();

    private static IAiTool CreateRegisteredTool(Type type)
    {
        if (type != typeof(AskTool))
            return (IAiTool)RuntimeHelpers.GetUninitializedObject(type);
        var mediator = Substitute.For<IMediator>();
        var habits = Substitute.For<IGenericRepository<Habit>>();
        var dates = Substitute.For<IUserDateService>();
        var planner = new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(),
            Substitute.For<IUnitOfWork>());
        return new AskTool(new BulkCreateHabitsTool(mediator), new BulkLogHabitsTool(mediator, habits, dates, planner),
            new BulkSkipHabitsTool(mediator, habits, dates, planner), dates);
    }

    [Fact]
    public async Task Requests_TwoUsersAndTwoTurns_ShareStaticMessageAndToolsForEachNegotiatedCapability()
    {
        var transport = new SequenceHandler(ResponsesTestData.Text("done"));
        var service = BuildService(transport);
        var tools = RegisteredTools();
        var users = new[] { User.Create("private-person-a", "a@example.com").Value,
            User.Create("private-person-b", "b@example.com").Value };
        users[0].SetLanguage("en-US");
        users[1].SetLanguage("pt-BR");
        var prefixes = new Dictionary<bool, string>();
        string? staticMessage = null;
        for (var index = 0; index < 4; index++)
        {
            var user = users[index / 2];
            var date = new DateOnly(2026, 1, 1).AddDays(index);
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user.Id, $"private-habit-{index}", null, null, DueDate: date)).Value;
            var handler = BuildHandler(service, tools, user, [habit], date);
            var command = new ProcessUserChatCommand(user.Id,
                index % 2 == 0 ? "Hello AI" : "sincronizar com o Google e duplicar meu hábito",
                History: [new("user", $"private-history-{index}")],
                ClientContext: index % 2 == 0 ? null : new AgentClientContext(
                    SupportsParts: true, PartKinds: ["choice", "pickList"], SupportsFollowUps: true));

            (await handler.Handle(command, default)).IsSuccess.Should().BeTrue();

            using var document = JsonDocument.Parse(transport.Bodies[^1]);
            var root = document.RootElement;
            var input = root.GetProperty("input");
            var body = transport.Bodies[^1];
            body.IndexOf("\"tools\":", StringComparison.Ordinal).Should().BeLessThan(body.IndexOf("\"input\":", StringComparison.Ordinal));
            body.IndexOf(input[0].GetRawText(), StringComparison.Ordinal).Should().BeLessThan(body.IndexOf(input[1].GetRawText(), StringComparison.Ordinal));
            var names = root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToList();
            var supportsAsk = index % 2 != 0;
            names.Should().HaveCount(supportsAsk ? 71 : 70).And.Contain("suggest_habits");
            names.Contains("ask").Should().Be(supportsAsk);
            names.Where(name => name != "ask").Should().Equal(tools.Select(tool => tool.Name)
                .Where(name => name != "ask").Order(StringComparer.Ordinal));
            var currentPrefix = CacheablePrefix(root);
            prefixes.TryAdd(supportsAsk, currentPrefix);
            Encoding.UTF8.GetBytes(currentPrefix).Should().Equal(Encoding.UTF8.GetBytes(prefixes[supportsAsk]));
            staticMessage ??= input[0].GetRawText();
            input[0].GetRawText().Should().Be(staticMessage);
            input.GetArrayLength().Should().Be(4);
            input[0].GetProperty("role").GetString().Should().Be("system");
            input[1].GetRawText().Should().Contain($"private-habit-{index}").And.Contain(date.ToString("yyyy-MM-dd"));
            input[1].GetRawText().Should().Contain($"Language: {user.Language}");
            input[2].GetRawText().Should().Contain($"private-history-{index}");
            input[3].GetProperty("role").GetString().Should().Be("user");
            currentPrefix.Should().NotContain("private-").And.NotContain(user.Id.ToString())
                .And.NotContain(date.ToString("yyyy-MM-dd")).And.NotContain($"Language: {user.Language}");
            root.GetProperty("prompt_cache_key").GetString().Should().Be(user.Id.ToString("N"));
        }
        prefixes.Should().HaveCount(2);
        prefixes[false].Should().NotBe(prefixes[true]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedTurn_EmitsOneGenerationAndRespectsConversationFlag(bool recordingEnabled)
    {
        var analytics = Substitute.For<IProductAnalytics>();
        var dailyWrites = new DailyWriteCapture();
        using var services = new ServiceCollection().AddSingleton(analytics)
            .AddDbContext<OrbitDbContext>(options => options.UseSqlite("Data Source=:memory:").AddInterceptors(dailyWrites))
            .BuildServiceProvider();
        var usage = ActivatorUtilities.CreateInstance<AiUsageRecorder>(services,
            Options.Create(new AiSettings()), NullLogger<AiUsageRecorder>.Instance);
        var transport = new SequenceHandler(ResponsesTestData.Text("Your reply"));
        var user = User.Create("Person", "recording@example.com").Value;
        var recorder = Substitute.For<IConversationTurnRecorder>();
        var handler = BuildHandler(BuildService(transport, usage), RegisteredTools(), user, [],
            new DateOnly(2026, 1, 1), recordingEnabled, recorder, analytics);

        var result = await handler.Handle(new(user.Id, "Hello AI", CorrelationId: "recorded-turn"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Your reply");
        transport.Bodies.Should().ContainSingle();
        dailyWrites.Count.Should().Be(1);
        analytics.Received(1).CaptureUserEvent(user.Id, "$ai_generation", user.Plan.ToString(),
            Arg.Is<IReadOnlyDictionary<string, object>>(properties =>
                (string)properties["$ai_trace_id"] == "recorded-turn" && !(bool)properties["$ai_is_error"]));
        analytics.Received(1).CaptureUserEvent(user.Id, "astra_message_sent", user.Plan.ToString(),
            Arg.Is<IReadOnlyDictionary<string, object>>(properties => (int)properties["round_count"] == 1));
        if (recordingEnabled)
            recorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn => turn.UserId == user.Id
                && turn.UserContent == "Hello AI" && turn.AssistantContent == "Your reply"
                && turn.UsedModel && turn.ToolCallsJson == "[]"));
        else
            recorder.DidNotReceiveWithAnyArgs().TryRecord(default!);
    }

    [Fact]
    public async Task FixedTurns_SerializedRequestSizeAndCacheablePrefixEstimate()
    {
        string[] turns = ["obrigado", "log my run today", "create a habit every wednesday and sunday",
            "criar um hábito toda quarta e domingo", "log my run and show my progress", "bulk log all habits today"];
        var tools = RegisteredTools();
        var user = User.Create("Writer", "writer@example.com").Value;
        var transport = new SequenceHandler(ResponsesTestData.Text("done"));
        var handler = BuildHandler(BuildService(transport), tools, user, [], new DateOnly(2026, 1, 1));
        string? prefix = null;
        foreach (var turn in turns)
        {
            (await handler.Handle(new(user.Id, turn), default)).IsSuccess.Should().BeTrue();
            var body = transport.Bodies[^1];
            using var document = JsonDocument.Parse(body);
            var currentPrefix = CacheablePrefix(document.RootElement);
            prefix ??= currentPrefix;
            currentPrefix.Should().Be(prefix);
            var requestBytes = Encoding.UTF8.GetByteCount(body);
            var prefixBytes = Encoding.UTF8.GetByteCount(currentPrefix);
            output.WriteLine($"{turn}: request {requestBytes} bytes (~{requestBytes / 4d:F2} tokens); "
                + $"cacheable prefix {prefixBytes} bytes (~{prefixBytes / 4d:F2} tokens); {tools.Count} tools. Estimate: UTF-8 bytes / 4, not provider token usage.");
        }
    }

    private static string CacheablePrefix(JsonElement request) =>
        request.GetProperty("tools").GetRawText() + request.GetProperty("input")[0].GetRawText();

    private static ProcessUserChatCommandHandler BuildHandler(IAiIntentService service,
        IReadOnlyList<IAiTool> tools, User user, IReadOnlyList<Habit> habits, DateOnly date,
        bool recordingEnabled = false, IConversationTurnRecorder? recorder = null, IProductAnalytics? analytics = null)
    {
        var habitRepo = Substitute.For<IGenericRepository<Habit>>();
        habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(habits);
        habitRepo.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<ChatHabitCandidate>>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<ChatHabitCandidate>)call.Arg<Func<IQueryable<Habit>, IQueryable<ChatHabitCandidate>>>()(
                habits.AsQueryable().Where(call.Arg<Expression<Func<Habit, bool>>>())).ToArray());
        var goalRepo = Substitute.For<IGenericRepository<Goal>>();
        goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Goal>());
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var tags = Substitute.For<IGenericRepository<Tag>>();
        tags.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Tag>());
        var templates = Substitute.For<IGenericRepository<ChecklistTemplate>>();
        templates.FindAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ChecklistTemplate>());
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(recordingEnabled ? [FeatureFlagKeys.AstraConversationRecording] : Array.Empty<string>());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(date);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(user.Id, date, Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(user.Id, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var logs = Substitute.For<IHabitScheduleLogReader>();
        logs.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<HabitScheduleLogDay>());
        return new ProcessUserChatCommandHandler(
            new(habitRepo, goalRepo, users, tags, templates, flags),
            new(service, new AiToolRegistry(tools), new SystemPromptBuilder(), new AgentCatalogService()),
            new(dates, Substitute.For<IUserStreakService>(), quota, Substitute.For<IUnitOfWork>(),
                Substitute.For<IServiceScopeFactory>(), Substitute.For<IAgentOperationExecutor>(),
                Substitute.For<IPendingClarificationStore>(), progress, Substitute.For<IGamificationService>(),
                Substitute.For<IMediator>(), analytics ?? Substitute.For<IProductAnalytics>(), logs,
                Substitute.For<IIssuedAstraBlockStore>(), Substitute.For<IPendingAgentOperationStore>()),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), ModerationTestDouble.Clean(), recorder);
    }

    private static AiIntentService BuildService(HttpMessageHandler handler, IAiUsageRecorder? usage = null, AiSettings? settings = null)
    {
        var responses = new ResponsesClient(new ApiKeyCredential("test"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        });
        usage ??= Substitute.For<IAiUsageRecorder>();
        return new AiIntentService(AiBudgetTestDouble.Wrap(new AiCompletionClient(responses, NullLogger<AiCompletionClient>.Instance, usage)),
            usage, NullLogger<AiIntentService>.Instance, settings is null ? null : Options.Create(settings));
    }

    private sealed class DailyWriteCapture : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }
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
