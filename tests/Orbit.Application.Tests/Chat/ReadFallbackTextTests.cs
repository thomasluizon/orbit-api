using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public class ReadFallbackTextTests
{
    private static readonly DateOnly From = new(2026, 10, 20);
    private static readonly DateOnly To = new(2026, 10, 26);
    private static readonly PeriodHabitMetrics Metrics = new(new(From, To), 6, 4, 3, 7, 42.86m, 2, 4, CompletedHabits: 3);

    public static IEnumerable<object[]> Branches()
    {
        foreach (var language in new[] { "en", "pt-BR" })
        {
            var pt = language == "pt-BR";
            foreach (var metric in new[] { "positiveLogRecords", "completedHabits", "distinctLoggedDates", "metScheduledDates", "scheduledDates" })
            {
                var label = ErrorCopy.Resolve("chat.read." + metric, pt);
                yield return [language, new PeriodTotalCard(metric, "records", 6, Metrics, new("day", [])), label, "6", metric];
                yield return [language, new PeriodComparisonCard(metric, "days", Metrics, Metrics, 6, 3, 3, 1), label, "6", metric];
            }
            yield return [language, new HabitListCard("remaining", [new("id", "Read", null, 0, false, "overdue")], To), pt ? "Hábitos" : "Habits", "1", "overdue"];
            yield return [language, new MetricsCard("habit", 40, 9, 7, 4, 2, 4, true, "habit", HabitTitle: "Read", PeriodMetrics: Metrics), pt ? "Conclusões" : "Completions", "6", "positiveLogRecords"];
            yield return [language, new MetricsCard("week", 40, 6, 7, 4, 2, 4, true, "progress"), pt ? "Esta semana" : "This week", "6", "week"];
            yield return [language, new GoalListCard([new("id", "Read", 6, 10, "pages", null)]), pt ? "Metas" : "Goals", "6/10", "goals"];
            yield return [language, new GoalListCard([]), pt ? "Metas" : "Goals", "0", "goals"];
            yield return [language, new DaySummaryCard(To, 10, 6, 60, 0, 2), pt ? "Concluídos / agendados" : "Completed / scheduled", "6/10", "daySummary"];
            yield return [language, new StreakCard(6, 9, 2, 20, 30, To, false, [], []), pt ? "Sequência atual" : "Current streak", "6", "TotalXp"];
            yield return [language, new HabitMonthCard(From, To, To, 1, new([], [])), pt ? "Nenhum dado de hábitos" : "No habit data", "26", "habitMonth"];
            yield return [language, new CalendarCard([new("Meeting", "2026-10-26T02:00:00Z", null, false)], new(true, "ReconnectRequired", null)), pt ? "Reconecte o Google Agenda" : "Reconnect Google Calendar", "1", "ReconnectRequired"];
            yield return [language, new PeriodInsightCard("week", From, To, 60, 6, 7, 6, 10, 2, 4, [], [], new("", "", "", ""), null), pt ? "Concluídos / agendados" : "Completed / scheduled", "6/10", "week"];
            foreach (var kind in new[] { "notifications", "tags", "templates", "keys" })
                yield return [language, new RecordListCard(kind, 6, [new("id", "Reading", State: "revoked")]), ErrorCopy.Resolve("chat.read." + kind, pt), "6", kind];
            var history = new RecordListCard("habitHistory", 6, [new("id", "2026-10-20", LogDate: From, Value: 2.5m)],
                Read: new(Guid.NewGuid(), "get_habit_history", JsonSerializer.SerializeToElement(new { date_from = "2026-10-20", date_to = "2026-10-26" }), "revision", "complete", "token"));
            yield return [language, history, pt ? "Histórico do hábito" : "Habit history", pt ? "2,5" : "2.5", "habitHistory"];
            yield return [language, new AccountRowsCard("profile", [new("astraAllowance", "6/10", "count"), new("trialEnd", "2026-10-26T02:00:00Z", "date"), new("aiSummary", "true", "boolean")]), pt ? "Mensagens da Astra" : "Astra messages", "6/10", "astraAllowance"];
        }
    }

    [Theory]
    [MemberData(nameof(Branches))]
    public void EveryBranchUsesLocalizedLabelsAndFigures(string language, object block, string label, string figure, string identifier)
    {
        var text = ProcessUserChatCommandHandler.ReadFallback(block, language, "America/Sao_Paulo");
        text.Should().Contain(label).And.Contain(figure).And.NotMatchRegex("(?m)^" + Regex.Escape(identifier) + ":").And.NotContain("(" + identifier + ")")
            .And.NotMatchRegex(@"\d{4}-\d{2}-\d{2}")
            .And.NotMatchRegex(@"(?m)^(All habits|Todos os hábitos|Habit calendar|Calendário de hábitos)(:|$)");
    }

    [Theory]
    [MemberData(nameof(Branches))]
    public void StoredBlocksKeepTurnLocaleWhenDowngraded(string language, object block, string label, string figure, string identifier)
    {
        var (_, kind, _) = ReadBlockTelemetry.Describe(block);
        var original = new ChatResponse(null, [], Parts: [new ChatBlockPart(kind, block)]);
        var replay = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(original, ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
        var result = ChatResponseNegotiation.ForClient(replay, new(SupportsParts: true, PartKinds: []), [], language, "America/Sao_Paulo");
        result.AiMessage.Should().Contain(label).And.Contain(figure)
            .And.NotMatchRegex("(?m)^" + Regex.Escape(identifier) + ":")
            .And.NotMatchRegex(@"\d{4}-\d{2}-\d{2}")
            .And.NotMatchRegex(@"(?m)^(All habits|Todos os hábitos|Habit calendar|Calendário de hábitos)(:|$)");
    }

    [Theory]
    [InlineData("en", "10/25/2026", "Sunday", "Yes", "Monthly")]
    [InlineData("pt-BR", "25/10/2026", "domingo", "Sim", "Mensal")]
    public void AccountValuesUseLocalizedDatesEnumsAndUserTimezone(string language, string date, string day, string yes, string interval)
    {
        var rows = new AccountRowsCard("plan", [new("trialEnd", "2026-10-26T02:00:00Z", "date"),
            new("weekStartDay", "0", "enum"), new("aiSummary", "true", "boolean"), new("interval", "monthly", "enum")]);
        var text = ProcessUserChatCommandHandler.ReadFallback(rows, language, "America/Sao_Paulo");
        text.Should().Contain(date).And.Contain(day).And.Contain(yes).And.Contain(interval)
            .And.NotContain("trialEnd").And.NotContain("weekStartDay").And.NotContain("true").And.NotContain("monthly");
    }

    [Theory]
    [InlineData("en", "10/20/2026", "10/26/2026")]
    [InlineData("pt-BR", "20/10/2026", "26/10/2026")]
    public void PeriodFallbackUsesLocaleDateOrder(string language, string from, string to)
    {
        ProcessUserChatCommandHandler.ReadFallback(new PeriodTotalCard("completedHabits", "habits", 6, Metrics, new("day", [])), language)
            .Should().Contain(from).And.Contain(to);
    }

    [Theory]
    [InlineData("en", 26, 26, "Today")]
    [InlineData("pt-BR", 26, 26, "Hoje")]
    [InlineData("en", 20, 20, "10/20/2026")]
    [InlineData("pt-BR", 20, 20, "20/10/2026")]
    [InlineData("en", 27, 27, "10/27/2026")]
    [InlineData("pt-BR", 27, 27, "27/10/2026")]
    [InlineData("en", 20, 26, "10/20/2026 to 10/26/2026")]
    [InlineData("pt-BR", 20, 26, "20/10/2026 a 26/10/2026")]
    public void PeriodLabelsUseAccountTodayOrOneDateAndKeepMultiDayRanges(string language, int start, int end, string label)
    {
        var from = new DateOnly(2026, 10, start);
        var to = new DateOnly(2026, 10, end);
        var metrics = Metrics with { Window = new(from, to) };
        var history = new ReadBlockMetadata(Guid.NewGuid(), "get_habit_history",
            JsonSerializer.SerializeToElement(new { date_from = from, date_to = to }), "revision", "complete", "token");
        object[] blocks =
        [
            new DaySummaryCard(from, 10, 6, 60, 0, 2, DateTo: to),
            new PeriodTotalCard("completedHabits", "habits", 6, metrics, new("day", [])),
            new PeriodComparisonCard("completedHabits", "habits", metrics, metrics, 6, 3, 3, 1),
            new MetricsCard("habit", 40, 9, 7, 4, 2, 4, true, "habit", HabitTitle: null, PeriodMetrics: metrics),
            new PeriodInsightCard("week", from, to, 60, 6, 7, 6, 10, 2, 4, [], [], new("", "", "", ""), null),
            new HabitMonthCard(from, to, To, 1, new([], [])),
            new RecordListCard("habitHistory", 0, [], Read: history)
        ];
        foreach (var block in blocks)
        {
            var text = ProcessUserChatCommandHandler.ReadFallback(block, language, "America/Sao_Paulo", To);
            text.Split('\n')[0].Should().Contain(label, "{0} must use the shared period label", block.GetType().Name);
            if (start == end)
                text.Should().NotContain(" to ").And.NotContain(" a ");
            var (_, kind, _) = ReadBlockTelemetry.Describe(block);
            var stored = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(
                new ChatResponse(null, [], Parts: [new ChatBlockPart(kind, block)]), ChatStreamEvent.SerializerOptions),
                ChatStreamEvent.SerializerOptions)!;
            var replay = ChatResponseNegotiation.ForClient(stored, new(SupportsParts: true, PartKinds: []), [],
                language, "America/Sao_Paulo", To);
            replay.AiMessage.Should().Be(text);
        }
        if (start == end)
            ProcessUserChatCommandHandler.ReadFallback(new DaySummaryCard(from, 10, 6, 60, 0, 2), language,
                "America/Sao_Paulo", To).Should().StartWith(label + ":");
    }
}
