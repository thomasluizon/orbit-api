using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class ProactiveOfferCopyTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void ActionsUseApprovedLabelsAndPersonMessages(string locale)
    {
        var pt = locale == "pt-BR";
        var today = new DateOnly(2026, 10, 30);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, Guid.NewGuid(), "Water", FrequencyUnit.Day, 1,
            today.AddDays(-14), CreatedAtUtc: today.AddDays(-14).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        var account = new ProactiveAccountFacts(today, 1, [habit], [], [], true, false, true);
        var due = new HabitListCard("today", [new(habit.Id.ToString(), habit.Title, null, 0, false, "today")], today);
        var overdue = due with { Items = [due.Items[0] with { Status = "overdue" }] };
        var metrics = new MetricsCard("week", 0, 0, 7, 0, 0, 0, true, "progress", HabitId: habit.Id);
        var goals = new GoalListCard([new(Guid.NewGuid().ToString(), "Water", 1, 10, "sessions", null, "behind", Status: "Active")]);
        RecordListCard Record(string kind) => new(kind, 1, [new(Guid.NewGuid().ToString(), "Record", IsRead: false)],
            Connection: new(true, "Workspace", nameof(NotionConnectionStatus.Idle), null, []));

        Check(due, "log", "Log this habit", "Registrar hábito", "Log Water for 10/30/2026.", "Registre Water em 30/10/2026.");
        Check(metrics, "reviewHabit", "Review this habit", "Rever este hábito",
            "Suggest one useful change to Water and keep the rest as it is.", "Sugira um ajuste útil em Water e mantenha o resto como está.");
        Check(overdue, "restart", "Restart gently", "Retomar aos poucos",
            "Help me restart my overdue habits: just a few today and new dates for the rest.",
            "Me ajude a retomar meus hábitos atrasados: só alguns hoje e novas datas para o resto.");
        Check(overdue, "skip", "Skip", "Pular", "Skip Water on 10/29/2026.", "Pule Water em 29/10/2026.");
        Check(overdue, "reminder", "Add a reminder", "Adicionar lembrete",
            "Suggest a reminder for Water without changing anything else.", "Sugira um lembrete para Water sem mudar o resto.");
        Check(goals, "goalLink", "Link a habit", "Vincular hábito",
            "Suggest a habit to link to my goal Water.", "Sugira um hábito para vincular à minha meta Water.");
        Check(goals, "goalTarget", "Adjust the target", "Ajustar a meta",
            "Suggest a new target for my goal Water.", "Sugira um novo valor para a minha meta Water.");
        Check(new HabitListCard("today", [], today, TotalCount: 0), "smallStep", "Choose one small step", "Escolher um passo pequeno",
            "Help me choose one small step I can take today.", "Me ajude a escolher um passo pequeno para hoje.");
        Check(Record("notionTasks"), "notion", "Plan in Orbit", "Planejar no Orbit",
            "Turn my Notion task Record into an Orbit task or habit.", "Transforme minha tarefa do Notion Record em uma tarefa ou hábito no Orbit.");
        Check(Record("notifications"), "notification", "Mark as read", "Marcar como lido",
            "Mark the alert Record as read.", "Marque o aviso Record como lido.");
        Check(Record("tags"), "reuse", "Use with a habit", "Usar em um hábito",
            "Suggest a habit to use my tag Record with.", "Sugira um hábito para usar a etiqueta Record.");
        Check(Record("templates"), "reuse", "Use with a habit", "Usar em um hábito",
            "Suggest a habit to use my checklist template Record with.", "Sugira um hábito para usar o modelo de checklist Record.");
        Check(due, "aim", "Plan this goal", "Planejar esta meta",
            "Turn what we just talked about into a goal.", "Transforme o que acabamos de conversar em uma meta.");
        Check(due, "calendar", "Connect Google Calendar", "Conectar Google Agenda", null, null);
        Check(new AccountRowsCard("profile", [new("aiSummary", "false", "boolean")]), "summary",
            "Review Astra settings", "Ver configurações da Astra", null, null);
        Check(due, "inspect", "View details", "Ver detalhes", null, null);

        void Check(object card, string family, string enLabel, string brLabel, string? enPrompt, string? brPrompt)
        {
            var read = new QueryReadBlock("test", card, new(Guid.NewGuid(), "query_habits",
                JsonSerializer.SerializeToElement(new { }), "revision", "complete", "token"));
            var action = ProactiveOfferPlanner.Build([read], account, locale).Single(fact => fact.Family == family).Action;
            action.Label.Should().Be(pt ? brLabel : enLabel);
            action.Prompt.Should().Be(pt ? brPrompt : enPrompt);
        }
    }

    [Theory]
    [InlineData("en", "Skip the day I missed Water.")]
    [InlineData("pt-BR", "Pule o dia em que perdi Water.")]
    public void MissedDateOutsideRecentWindowUsesApprovedPrompt(string locale, string prompt)
    {
        var today = new DateOnly(2026, 10, 30);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, Guid.NewGuid(), "Water", FrequencyUnit.Day, 30,
            today.AddDays(-20), CreatedAtUtc: today.AddDays(-20).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        var read = new QueryReadBlock("habitList", new HabitListCard("today", [new(habit.Id.ToString(), habit.Title, null, 0, false, "overdue")], today),
            new(Guid.NewGuid(), "query_habits", JsonSerializer.SerializeToElement(new { }), "revision", "complete", "token"));
        var action = ProactiveOfferPlanner.Build([read], new(today, 1, [habit], [], [], false, false), locale)
            .Single(fact => fact.Family == "skip").Action;
        action.Date.Should().BeNull();
        action.Prompt.Should().Be(prompt);
    }
}
