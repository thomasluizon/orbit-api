#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Linq.Expressions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiInputBudgetTests
{
    [Theory]
    [InlineData("legacy", false)]
    [InlineData("composition", false)]
    [InlineData("offers", false)]
    [InlineData("legacy", true)]
    [InlineData("composition", true)]
    [InlineData("offers", true)]
    public async Task CacheRequests_LiveSequence_PreservesAccountPrefixAndWarmCost(string client, bool history)
    {
        var user = User.Create("Cache account", "cache-sequence@example.com").Value;
        SetCacheId(user, "user");
        var account = new LiveCacheAccount(user);
        var transport = new LiveSequenceTransport(account);
        var provider = BuildService(transport);
        var handler = BuildHandler(provider, RegisteredTools(), user, account.Habits, account.Today,
            enabledFlags: client == "legacy" ? [] : [FeatureFlagKeys.AstraBlockFirst], account: account);
        var context = client == "legacy" ? null : new AgentClientContext(Platform: "web", SupportsParts: true,
            PartKinds: ["choice", "pickList", "question", "habitList", "goalList", "metrics", "periodInsight", "daySummary",
                "streak", "calendar", "recordList", "accountRows", "habitMonth", "periodTotal", "periodComparison",
                "habitRanking", "oneSmallStep", "habitSuggestions", "gamification"], SupportsHabitListCard: true,
            SupportsPendingOperationChanges: true, SupportsProactiveOffers: client == "offers",
            ConversationId: Guid.Parse("11111111-1111-4111-8111-111111111111"), DismissedOfferKeys: []);
        string[] turns = ["What do I have today?",
            "Create a habit to drink water every day at 9:00 with two sub-habits: morning glass and afternoon glass.",
            "How did I do this week?", "Quais hábitos eu tenho pra amanhã?"];
        var prior = new List<ChatHistoryMessage>();
        var bodies = new List<string>();
        for (var index = 0; index < turns.Length; index++)
        {
            account.Turn = index;
            account.Clock.Advance(TimeSpan.FromSeconds(7));
            var start = transport.Bodies.Count;
            var result = await handler.Handle(new(user.Id, turns[index], History: history ? prior.ToList() : null,
                ClientContext: context is null ? null : context with { CurrentAppArea = index < 2 ? "today" : "progress" }), default);
            result.IsSuccess.Should().BeTrue();
            if (index == 1)
            {
                var preview = result.Value.PendingOperations.Should().ContainSingle().Subject;
                JsonSerializer.Serialize(preview.Items).Should().Contain("morning glass").And.Contain("afternoon glass");
            }
            bodies.Add(transport.Bodies[start]);
            prior.Add(new("user", turns[index]));
            prior.Add(new("assistant", "Done"));
        }

        var report = new StringBuilder();
        var costs = new List<decimal>();
        var warm = ModelInput(bodies[0]);
        var written = WrittenPrefixes(bodies[0]).ToList();
        for (var index = 0; index < bodies.Count; index++)
        {
            var current = ModelInput(bodies[index]);
            var earlier = index == 0 ? warm : ModelInput(bodies[index - 1]);
            var shared = CommonTokens(earlier, current);
            var reusable = CachedPrefixTokens(written, bodies[index]);
            var cached = reusable >= 1024 ? reusable / 128 * 128 : 0;
            var cost = ((Count(current) - cached) * 0.125m + cached * 0.01m + 204 * 0.50m) / 1_000_000;
            costs.Add(cost);
            written.AddRange(WrittenPrefixes(bodies[index]));
            var bytes = Encoding.UTF8.GetBytes(index == 0 ? bodies[0] : bodies[index - 1]);
            var next = Encoding.UTF8.GetBytes(bodies[index]);
            var offset = 0;
            while (offset < Math.Min(bytes.Length, next.Length) && bytes[offset] == next[offset]) offset++;
            var difference = Encoding.UTF8.GetString(next.Skip(offset).Take(160).ToArray());
            report.AppendLine(FormattableString.Invariant($"{client}, history={history}, turn={index + 1}: input={Count(current)}, first request difference={offset} UTF-8 bytes, common model prefix={shared} tokens, cached={cached}, warm=${cost:F7}; next bytes={difference}"));
        }
        report.AppendLine(FormattableString.Invariant($"Warm mean=${costs.Average():F7}"));
        var allWritten = WrittenPrefixes(bodies[0]).ToList();
        var allCosts = new List<decimal>();
        foreach (var body in transport.Bodies)
        {
            var common = CachedPrefixTokens(allWritten, body);
            var cached = common >= 1024 ? common / 128 * 128 : 0;
            allCosts.Add(((Count(ModelInput(body)) - cached) * 0.125m + cached * 0.01m + 204 * 0.50m) / 1_000_000);
            allWritten.AddRange(WrittenPrefixes(body));
        }
        report.AppendLine(FormattableString.Invariant($"All {allCosts.Count} chat calls warm mean=${allCosts.Average():F7}"));
        output.WriteLine(report.ToString());
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), $"orbit-1916-live-{client}-{history}.txt"), report.ToString());
        costs.Average().Should().BeLessThanOrEqualTo(0.0009m, report.ToString());
        allCosts.Average().Should().BeLessThanOrEqualTo(0.0009m, report.ToString());
        for (var index = 1; index < bodies.Count; index++)
        {
            StaticPrefix(bodies[index]).Should().Be(StaticPrefix(bodies[0]));
            var input = InputItems(bodies[index]);
            input[0].Should().Contain("prompt_cache_breakpoint");
            input[1].Should().Contain("prompt_cache_breakpoint").And.NotContain("Pending previews");
            var prefix = ModelInput(bodies[index]);
            var stableEnd = prefix.IndexOf("Recent habits:", StringComparison.Ordinal);
            CommonTokens(ModelInput(bodies[0]), prefix).Should().BeGreaterThan(Count(prefix[..stableEnd]),
                "the complete unchanged account index must precede turn-specific state and history");
        }
        bodies[2].Should().Contain("morning glass").And.Contain("afternoon glass");
        if (client == "offers")
        {
            await account.Offers.Received().IssueAsync(user.Id, context!.ConversationId!.Value, Arg.Any<AstraOfferFact>(), Arg.Any<CancellationToken>());
            account.IssuedCount.Should().BeGreaterThan(0, "the production offer planner must run between requests");
        }
    }

    private static void SetCacheId(Entity entity, string key) => typeof(Entity).GetProperty(nameof(Entity.Id))!
        .SetValue(entity, new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16)));

    private sealed class LiveCacheAccount
    {
        public CacheClock Clock { get; } = new();
        public DateOnly Today => DateOnly.FromDateTime(Clock.GetUtcNow().AddHours(-3).DateTime);
        public IReadOnlyList<Habit> Habits { get; }
        private IReadOnlyList<Goal> Goals { get; }
        private IReadOnlyList<Tag> Tags { get; }
        public IConversationOfferStore Offers { get; } = Substitute.For<IConversationOfferStore>();
        public int Turn { get; set; }
        private readonly List<AstraOfferFact> _issued = [];
        public int IssuedCount => _issued.Count;
        public JsonElement CreationArguments { get; private set; }
        private readonly User _user;

        public LiveCacheAccount(User user)
        {
            _user = user;
            Tags = Enumerable.Range(0, 8).Select(index => Tag.Create(user.Id, $"Area {index}", "#00ff00").Value).ToArray();
            for (var index = 0; index < Tags.Count; index++) SetCacheId(Tags[index], "tag-" + index);
            Goals = Enumerable.Range(0, 6).Select(index => Goal.Create(new(user.Id, $"Goal {index}", 100, "sessions",
                Description: $"Keep a sustainable routine in area {index}", Deadline: Today.AddMonths(3))).Value).ToArray();
            for (var index = 0; index < Goals.Count; index++)
            {
                SetCacheId(Goals[index], "goal-" + index);
                Goals[index].UpdatedAtUtc = Clock.GetUtcNow().UtcDateTime;
            }
            var habits = new List<Habit>();
            for (var index = 0; index < 24; index++)
            {
                var parent = Habit.Create(new(HabitSchedule.Recurring, user.Id, $"Daily routine {index}", FrequencyUnit.Day, 1,
                    Today, DueTime: new TimeOnly(9, 0), ReminderEnabled: true, StartDate: Today.AddDays(-14))).Value;
                SetCacheId(parent, "parent-" + index);
                parent.AddTag(Tags[index % Tags.Count]);
                parent.AddGoal(Goals[index % Goals.Count]);
                parent.UpdatedAtUtc = Clock.GetUtcNow().UtcDateTime;
                habits.Add(parent);
                for (var child = 0; child < 2; child++)
                {
                    var habit = Habit.Create(new(HabitSchedule.Recurring, user.Id, $"Routine {index} step {child}", FrequencyUnit.Day, 1,
                        Today, ParentHabitId: parent.Id, DueTime: new TimeOnly(9, 0), ReminderEnabled: true,
                        StartDate: Today.AddDays(-14))).Value;
                    SetCacheId(habit, $"child-{index}-{child}");
                    habit.UpdatedAtUtc = Clock.GetUtcNow().UtcDateTime;
                    habit.AddTag(Tags[(index + child) % Tags.Count]);
                    habits.Add(habit);
                }
            }
            Habits = habits;
            Offers.IssuedAsync(user.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => _issued.ToArray());
            Offers.DismissedAsync(user.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new HashSet<string>());
            Offers.IssueAsync(user.Id, Arg.Any<Guid>(), Arg.Any<AstraOfferFact>(), Arg.Any<CancellationToken>())
                .Returns(call => { _issued.Add(call.Arg<AstraOfferFact>()); return Task.CompletedTask; });
        }

        private T[] Rows<T>(IReadOnlyList<T> rows) => rows.Skip(Turn).Concat(rows.Take(Turn)).ToArray();

        public void Configure(IGenericRepository<Habit> habits, IGenericRepository<Goal> goals, IGenericRepository<Tag> tags,
            IUserDateService dates, IGenericRepository<User> users, IHabitScheduleLogReader logs,
            IPendingAgentOperationStore pending, IAgentOperationExecutor executor, PendingOperationChangePreviewer previewer)
        {
            habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToArray());
            habits.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<ChatHabitCandidate>>>(), Arg.Any<CancellationToken>())
                .Returns(call => (IReadOnlyList<ChatHabitCandidate>)call.Arg<Func<IQueryable<Habit>, IQueryable<ChatHabitCandidate>>>()(Rows(Habits).AsQueryable()).ToArray());
            goals.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(), Arg.Any<CancellationToken>()).Returns(_ => Rows(Goals));
            tags.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>()).Returns(_ => Rows(Tags));
            dates.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_ => Today);
            CreationArguments = JsonSerializer.SerializeToElement(new
            {
                title = "Drink water",
                frequency_unit = "Day",
                frequency_quantity = 1,
                due_date = Today.ToString("yyyy-MM-dd"),
                due_time = "09:00",
                sub_habits = new[] { new { title = "morning glass" }, new { title = "afternoon glass" } }
            });
            var execution = new PendingAgentOperationExecution(Guid.NewGuid(), AgentCapabilityIds.HabitsWrite, "create_habit", CreationArguments,
                AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation);
            pending.GetAwaiting(_user.Id).Returns(_ => Turn >= 2 ? new[] { execution } : []);
            pending.GetExecution(_user.Id, execution.PendingOperationId).Returns(execution);
            pending.IsAwaitingConfirmation(_user.Id, execution.PendingOperationId).Returns(true);
            var blocks = new ReadBlockFactory(dates, Substitute.For<IReadQueryTokenService>(), users);
            executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var request = call.Arg<AgentExecuteOperationRequest>();
                if (request.OperationId == "create_habit")
                {
                    var preview = (await previewer.PreviewAsync(_user.Id, "create_habit", CreationArguments, default)).Value!;
                    var held = new PendingAgentOperation(execution.PendingOperationId, AgentCapabilityIds.HabitsWrite,
                        "Create habit", "Drink water", AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation,
                        Clock.GetUtcNow().UtcDateTime.AddHours(1), preview.Changes, preview.ChangeTargetCount, preview.Items, "water-preview");
                    return new AgentExecuteOperationResponse(new("create_habit", "create_habit", AgentRiskClass.Low,
                        AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), held);
                }
                var card = new HabitListCard("today", Habits.Select(habit => new HabitListCardItem(habit.Id.ToString(),
                    habit.Title, null, 0, false, "today")).ToArray(), Today, TotalCount: Habits.Count);
                return new AgentExecuteOperationResponse(new(request.OperationId, request.OperationId, AgentRiskClass.Low,
                    AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, Payload: card,
                    ReadBlock: blocks.Capture(request.OperationId, request.Arguments, _user.Id, card)));
            });
        }
    }

    private sealed class LiveSequenceTransport(LiveCacheAccount account) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        private int _previousTurn = -1;
        private int _round;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            if (_previousTurn != account.Turn) { _round = 0; _previousTurn = account.Turn; }
            var response = ResponsesTestData.Response("Done", outputTokens: 204);
            if (_round++ == 0)
            {
                response.OutputItems.Clear();
                var write = account.Turn == 1;
                var writeArguments = account.CreationArguments.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value);
                writeArguments[TerminalWriteDirective.CompleteArgument] = true;
                var arguments = write ? JsonSerializer.SerializeToElement(writeArguments)
                    : JsonSerializer.SerializeToElement(new { date = "today", for_selection = false, orbit_read_complete = true });
                response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("sequence-call-" + account.Turn,
                    write ? "create_habit" : "query_habits", BinaryData.FromString(arguments.GetRawText())));
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(ModelReaderWriter.Write(response).ToString(), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class CacheClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 15, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
