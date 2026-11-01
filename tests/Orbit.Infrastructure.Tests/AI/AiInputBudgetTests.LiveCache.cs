#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Linq.Expressions;
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
        var account = new LiveCacheAccount(user);
        var transport = new SequenceHandler(ResponsesTestData.Text("Done", outputTokens: 204));
        var provider = BuildService(transport);
        var capture = Substitute.For<IAiIntentService>();
        AiToolRequest? request = null;
        capture.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                request = call.Arg<AiToolRequest>();
                (await provider.SendWithToolsAsync(request)).IsSuccess.Should().BeTrue();
                return Result.Success(new AiResponse { TextMessage = "Done" });
            });
        var handler = BuildHandler(capture, RegisteredTools(), user, account.Habits, account.Today,
            enabledFlags: client == "legacy" ? [] : [FeatureFlagKeys.AstraBlockFirst], account: account);
        var context = client == "legacy" ? null : new AgentClientContext(Platform: "web", SupportsParts: true,
            PartKinds: ["choice", "pickList", "question", "habitList", "goalList", "metrics", "periodInsight", "daySummary",
                "streak", "calendar", "recordList", "accountRows", "habitMonth", "periodTotal", "periodComparison",
                "habitRanking", "oneSmallStep", "checkIn", "restart"], SupportsHabitListCard: true,
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
            (await handler.Handle(new(user.Id, turns[index], History: history ? prior.ToList() : null,
                ClientContext: context), default)).IsSuccess.Should().BeTrue();
            request.Should().NotBeNull();
            bodies.Add(transport.Bodies[^1]);
            prior.Add(new("user", turns[index]));
            prior.Add(new("assistant", "Done"));
        }

        var report = new StringBuilder();
        var costs = new List<decimal>();
        var warm = ModelInput(bodies[0]);
        for (var index = 0; index < bodies.Count; index++)
        {
            var current = ModelInput(bodies[index]);
            var earlier = index == 0 ? warm : ModelInput(bodies[index - 1]);
            var shared = CommonTokens(earlier, current);
            var cached = shared >= 1024 ? shared / 128 * 128 : 0;
            var cost = ((Count(current) - cached) * 0.125m + cached * 0.01m + 204 * 0.50m) / 1_000_000;
            costs.Add(cost);
            var bytes = Encoding.UTF8.GetBytes(earlier);
            var next = Encoding.UTF8.GetBytes(current);
            var offset = 0;
            while (offset < Math.Min(bytes.Length, next.Length) && bytes[offset] == next[offset]) offset++;
            var difference = Encoding.UTF8.GetString(next.Skip(offset).Take(160).ToArray());
            report.AppendLine(FormattableString.Invariant($"{client}, history={history}, turn={index + 1}: input={Count(current)}, first difference={offset} UTF-8 bytes/{shared} tokens, warm=${cost:F7}; next bytes={difference}"));
        }
        report.AppendLine(FormattableString.Invariant($"Warm mean=${costs.Average():F7}"));
        output.WriteLine(report.ToString());
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), $"orbit-1916-live-{client}-{history}.txt"), report.ToString());
        costs.Average().Should().BeLessThanOrEqualTo(0.0009m, report.ToString());
        for (var index = 1; index < bodies.Count; index++)
        {
            StaticPrefix(bodies[index]).Should().Be(StaticPrefix(bodies[0]));
            var prefix = ModelInput(bodies[index]);
            var stableEnd = prefix.IndexOf("Recent habits:", StringComparison.Ordinal);
            CommonTokens(ModelInput(bodies[0]), prefix).Should().BeGreaterThan(Count(prefix[..stableEnd]),
                "the complete unchanged account index must precede turn-specific state and history");
        }
        bodies[2].Should().Contain("morning glass").And.Contain("afternoon glass");
        if (client == "offers")
            await account.Offers.Received().IssuedAsync(user.Id, context!.ConversationId!.Value, Arg.Any<CancellationToken>());
    }

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
        private readonly User _user;

        public LiveCacheAccount(User user)
        {
            _user = user;
            Tags = Enumerable.Range(0, 8).Select(index => Tag.Create(user.Id, $"Area {index}", "#00ff00").Value).ToArray();
            Goals = Enumerable.Range(0, 6).Select(index => Goal.Create(new(user.Id, $"Goal {index}", 100, "sessions",
                Description: $"Keep a sustainable routine in area {index}", Deadline: Today.AddMonths(3))).Value).ToArray();
            var habits = new List<Habit>();
            for (var index = 0; index < 24; index++)
            {
                var parent = Habit.Create(new(HabitSchedule.Recurring, user.Id, $"Daily routine {index}", FrequencyUnit.Day, 1,
                    Today, DueTime: new TimeOnly(9, 0), ReminderEnabled: true, StartDate: Today.AddDays(-14))).Value;
                parent.AddTag(Tags[index % Tags.Count]);
                parent.AddGoal(Goals[index % Goals.Count]);
                habits.Add(parent);
                for (var child = 0; child < 2; child++)
                {
                    var habit = Habit.Create(new(HabitSchedule.Recurring, user.Id, $"Routine {index} step {child}", FrequencyUnit.Day, 1,
                        Today, ParentHabitId: parent.Id, DueTime: new TimeOnly(9, 0), ReminderEnabled: true,
                        StartDate: Today.AddDays(-14))).Value;
                    habit.AddTag(Tags[(index + child) % Tags.Count]);
                    habits.Add(habit);
                }
            }
            Habits = habits;
            Offers.IssuedAsync(user.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => _issued.ToArray());
            Offers.DismissedAsync(user.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new HashSet<string>());
        }

        private T[] Rows<T>(IReadOnlyList<T> rows) => rows.Skip(Turn).Concat(rows.Take(Turn)).ToArray();

        public void Configure(IGenericRepository<Habit> habits, IGenericRepository<Goal> goals, IGenericRepository<Tag> tags,
            IUserDateService dates, IGenericRepository<User> users, IHabitScheduleLogReader logs,
            IPendingAgentOperationStore pending, IAgentOperationExecutor executor, PendingOperationChangePreviewer previewer)
        {
            habits.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<ChatHabitCandidate>>>(), Arg.Any<CancellationToken>())
                .Returns(call => (IReadOnlyList<ChatHabitCandidate>)call.Arg<Func<IQueryable<Habit>, IQueryable<ChatHabitCandidate>>>()(Rows(Habits).AsQueryable()).ToArray());
            goals.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(), Arg.Any<CancellationToken>()).Returns(_ => Rows(Goals));
            tags.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>()).Returns(_ => Rows(Tags));
            dates.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_ => Today);
            var arguments = JsonSerializer.SerializeToElement(new
            {
                title = "Drink water",
                frequency_unit = "Day",
                frequency_quantity = 1,
                due_date = Today.ToString("yyyy-MM-dd"),
                due_time = "09:00",
                sub_habits = new[] { new { title = "morning glass" }, new { title = "afternoon glass" } }
            });
            var execution = new PendingAgentOperationExecution(Guid.NewGuid(), AgentCapabilityIds.HabitsWrite, "create_habit", arguments,
                AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation);
            pending.GetAwaiting(_user.Id).Returns(_ => Turn >= 2 ? new[] { execution } : []);
            pending.GetExecution(_user.Id, execution.PendingOperationId).Returns(execution);
            pending.IsAwaitingConfirmation(_user.Id, execution.PendingOperationId).Returns(true);
        }
    }

    private sealed class CacheClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 15, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
