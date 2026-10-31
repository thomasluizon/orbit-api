#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Tests.Common;
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
    [InlineData("en", 1)]
    [InlineData("en", 2)]
    [InlineData("en", 3)]
    [InlineData("pt-BR", 1)]
    [InlineData("pt-BR", 2)]
    [InlineData("pt-BR", 3)]
    public async Task R9c_DailySummaryReplaysModelOutputThroughProducerAndComposition(string language, int repeat)
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "CompositionFailures", $"prod-r9c-composition-hoje-next-step-pt-BR-r{repeat}.json")));
        var user = User.Create("Person", "person@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        var habits = NextStepHabits();
        var titles = habits.Select(habit => habit.Title).ToArray();
        var lunch = habits.Single(habit => habit.Title == "Lunch");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(habits);
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var summaryLogs = Substitute.For<IHabitSummaryLogReader>();
        summaryLogs.ReadAsync(Arg.Any<IReadOnlyCollection<HabitSummaryLogWindow>>(), Arg.Any<CancellationToken>())
            .Returns(habits.SelectMany(habit => habit.Logs)
                .Select(log => new HabitSummaryLogFact(log.HabitId, log.Date, log.Value)).ToList());
        var schedule = DailySummaryScheduleFixture.Project(habits, Today);
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PaginatedResponse<HabitScheduleItem>(schedule, 1, 100, schedule.Count, 1)));
        var sentence = capture.RootElement.GetProperty("summaryResult").GetProperty("summary").GetString()!;
        if (language == "en")
            sentence = repeat == 3
                ? "Breakfast is behind you; lunch is the next step because it comes before the other timed items."
                : "Lunch is the next step in your meal routine because it comes before the other items.";
        var modelJson = repeat == 3
            ? JsonSerializer.Serialize(new { summary = sentence })
            : JsonSerializer.Serialize(new { habitId = lunch.Id, summary = sentence });
        using var transport = new NextStepModelResponseHandler(modelJson);
        using var http = new HttpClient(transport);
        var ai = new AiCompletionClient(Options.Create(new AiSettings { ApiKey = "test-key", MaxRetries = 0 }),
            NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>(), new HttpClientPipelineTransport(http));
        var budgeted = new BudgetedAiCompletionClient(ai, Substitute.For<IAiDailyBudget>(), new HttpContextAccessor(),
            NullLogger<BudgetedAiCompletionClient>.Instance, Substitute.For<IAiRequestCostEstimator>());
        var summaries = new AiSummaryService(budgeted, NullLogger<AiSummaryService>.Instance);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var query = new GetDailySummaryQueryHandler(_habitRepo, _userRepo, Substitute.For<IGenericRepository<HabitLog>>(),
            summaryLogs, _payGate, summaries, cache, _userDateService, _mediator);
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => query.Handle(call.Arg<GetDailySummaryQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetDailySummaryTool(_mediator, _userDateService);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(new() { ToolCalls = BrainDumpCalls(capture), ConversationContext = new() });
        var client = new AgentClientContext(SupportsParts: true, SupportsDaySummaryCard: true,
            SupportsHabitListDoneStatus: true, PartKinds: ["daySummary"], Locale: language);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().NotBeNull();
        var parts = result.Value.Parts!;
        var block = parts.OfType<ChatBlockPart>().Should().ContainSingle().Subject;
        block.Kind.Should().Be("daySummary");
        var day = block.Block.Should().BeOfType<DaySummaryCard>().Subject;
        day.Rows.Should().HaveCount(12).And.Contain(row => row.Title == "Breakfast" && row.Status == "done")
            .And.Contain(row => row.Title == "Lunch").And.Contain(row => row.Title == "Afternoon snack");
        if (repeat == 3)
        {
            parts.Should().ContainSingle("the captured completed-status recap must be dropped, leaving the typed block");
            result.Value.AiMessage.Should().BeNullOrEmpty();
        }
        else
        {
            parts.OfType<ChatTextPart>().Should().ContainSingle().Which.Text.Should().Be(sentence);
            result.Value.AiMessage.Should().Be(sentence);
        }
        events.Where(item => item.Type == "delta").Should().BeEmpty();
        transport.Requests.Should().Be(1);
        await AssertNoCompositionContinuation();
        AssertStoredComposition(result.Value, client, language);
        habits.Select(habit => habit.Title).Should().Equal(titles);
    }

    private sealed class NextStepModelResponseHandler(string modelJson) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new ResponseResult
            {
                Id = "resp_summary_replay",
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1700000000),
                Model = "gpt-6-luna",
                Status = ResponseStatus.Completed
            };
            response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem(modelJson));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(ModelReaderWriter.Write(response).ToString(), Encoding.UTF8, "application/json")
            });
        }
    }
}
