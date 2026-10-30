using System.Globalization;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed record ProactiveAccountFacts(DateOnly Today, int WeekStartDay, IReadOnlyList<Habit> Habits,
    IReadOnlyList<Goal> Goals, IReadOnlyList<HabitScheduleLogDay> LogDays,
    bool CalendarAvailable, bool CalendarConnected);

public static class ProactiveOfferPlanner
{
    public const string SelectionInstruction = "Select at most one useful next action from the supplied eligible typed candidates, or none. Interpret the person's intent from the request and facts, never from a keyword list. A dated_event candidate needs an actual dated-event intent; an aim candidate needs an expressed aim and no equivalent active goal in the complete account facts. Never offer productivity actions for crisis, refusal, billing verification or quota replies. Do not repeat a dismissed offer or add a prose question. Do not execute tools, invent candidates or output prose. Return only JSON with candidateId (an eligible id or null), interpretation (aim, dated_event or null), and dismissedOfferKeys (only supplied previously issued keys the person dismisses in this turn).";

    public static IReadOnlyList<AstraOfferFact> Build(IReadOnlyList<QueryReadBlock> reads,
        ProactiveAccountFacts account, string? language)
    {
        return reads.Where(read => read.Read.Completeness == "complete" && read.Read.Recovery is null)
            .SelectMany(read => new Builder(read, account, LocaleHelper.IsPortuguese(language)).Build())
            .DistinctBy(offer => offer.Id).ToArray();
    }

    private sealed class Builder(QueryReadBlock read, ProactiveAccountFacts account, bool pt)
    {
        private readonly List<AstraOfferFact> offers = [];
        public IReadOnlyList<AstraOfferFact> Build()
        {
            HabitOffers();
            GoalOffers();
            DayOffers();
            RecordOffers();
            OtherOffers();
            return offers;
        }
        private void Add(string family, string evidence, string en, string br, string enPrompt, string brPrompt,
                Guid? entity = null, string kind = "preparePreview", string? surface = null, DateOnly? date = null)
        {
            var key = family + ":" + (entity?.ToString() ?? surface ?? "account")
                + (date is { } day ? ":" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "");
            offers.Add(new(key, family, evidence,
                new(key, kind, pt ? br : en, surface, kind == "openSurface" ? null : pt ? brPrompt : enPrompt, entity, date),
                read.Read.QueryId, read.Read.Revision, account.Today)
            { Source = read.Read });
        }

        private IReadOnlyList<HabitListCardItem> HabitRows() => read.Block switch
        {
            HabitListCard list when list.Date is null || list.Date == account.Today => list.Items,
            DaySummaryCard day when day.Date == account.Today && (day.DateTo is null || day.DateTo == day.Date) => day.Rows ?? [],
            MetricsCard { HabitId: { } id } => account.Habits.Where(habit => habit.Id == id)
                .Select(habit => new HabitListCardItem(habit.Id.ToString(), habit.Title, habit.Emoji, 0, habit.IsBadHabit, "none")).ToArray(),
            PeriodInsightCard insight => account.Habits.Where(habit => insight.NeedsAttention.Any(item => item.HabitId == habit.Id))
                .Select(habit => new HabitListCardItem(habit.Id.ToString(), habit.Title, habit.Emoji, 0, habit.IsBadHabit, "none")).ToArray(),
            _ => []
        };

        private void HabitOffers()
        {
            var items = HabitRows();
            foreach (var item in items)
            {
                if (!Guid.TryParse(item.Id, out var id)) continue;
                var habit = account.Habits.SingleOrDefault(habit => habit.Id == id && !habit.IsDeleted);
                if (habit is null || habit.IsBadHabit || habit.IsCompleted || item.IsSkippedOnDate == true) continue;
                var days = account.LogDays.Where(day => day.HabitId == id).ToArray();
                var resolved = days.Where(day => day.CompletedCount > 0 || day.SkippedCount > 0).Select(day => day.Date).ToHashSet();
                if (item.Status == "today" && !resolved.Contains(account.Today)
                    && HabitScheduleService.WasScheduledOnDate(habit, account.Today, account.WeekStartDay))
                    Add("log", "A positive scheduled occurrence is due today and unresolved.", "Log this habit", "Registrar hábito",
                        $"Prepare a log preview for {habit.Title} ({id}) on {account.Today:yyyy-MM-dd}.",
                        $"Prepare uma prévia para registrar {habit.Title} ({id}) em {account.Today:yyyy-MM-dd}.", id, date: account.Today);
                if (read.Block is MetricsCard or PeriodInsightCard)
                    Add("reviewHabit", "A successful habit-specific metrics or attention result identifies this owned habit.",
                        "Review this habit", "Rever este hábito",
                        $"Read the current configuration of {habit.Title} ({id}) and propose a useful adjustment while preserving requested and stored fields.",
                        $"Leia a configuração atual de {habit.Title} ({id}) e sugira um ajuste útil, preservando os campos solicitados e salvos.", id, kind: "continueChat");
                var missed = MissedDates(habit, account.Today, account.WeekStartDay, days);
                if (item.Status == "overdue" && (habit.Schedule == HabitSchedule.OneTime && habit.NextDueDate < account.Today
                    || HabitScheduleService.HasMissedPastOccurrence(habit, account.Today, account.WeekStartDay, resolvedDates: resolved)))
                {
                    Add("restart", "An owned overdue or missed occurrence is unresolved.", "Restart gently", "Retomar aos poucos",
                        "Prepare a smaller restart plan from my current overdue habits, keeping only a small set today and giving deferred items concrete dates.",
                        "Prepare uma retomada menor com meus hábitos atrasados, mantendo poucos itens hoje e dando datas concretas aos demais.", id, date: account.Today);
                    Add("skip", "An owned overdue or missed occurrence is unresolved.", "Skip this occurrence", "Pular ocorrência",
                        $"Prepare a skip preview for {habit.Title} ({id}) on {account.Today:yyyy-MM-dd}.",
                        $"Prepare uma prévia para pular {habit.Title} ({id}) em {account.Today:yyyy-MM-dd}.", id, date: account.Today);
                }
                if (missed.Count >= 3 && !habit.ReminderEnabled)
                    Add("reminder", "Reminders are off and at least three scheduled occurrences were missed in the last fourteen local days.",
                        "Add a reminder", "Adicionar lembrete",
                        $"Prepare a reminder proposal for {habit.Title} ({id}); keep all its other settings.",
                        $"Prepare uma sugestão de lembrete para {habit.Title} ({id}); preserve as outras configurações.", id);
            }
        }

        private void GoalOffers()
        {
            if (read.Block is GoalListCard goals)
                foreach (var goal in goals.Items)
                    if (Guid.TryParse(goal.Id, out var id) && goal.Status == "Active"
                        && (goal.TrackingStatus is "behind" or "at_risk" || goals.OfferTracking?.GetValueOrDefault(id) is "behind" or "at_risk"))
                    {
                        Add("goalLink", "An active goal is behind or at risk with grounded progress and links.", "Link a habit", "Vincular hábito",
                            $"Read my current habits and propose a suitable link to goal {goal.Title} ({id}), preserving its existing links.",
                            $"Leia meus hábitos atuais e sugira um vínculo com a meta {goal.Title} ({id}), preservando os vínculos existentes.", id);
                        Add("goalTarget", "An active goal is behind or at risk with grounded progress and target.", "Adjust the target", "Ajustar objetivo",
                            $"Prepare an editable target adjustment for goal {goal.Title} ({id}); preserve the other fields.",
                            $"Prepare um ajuste editável do objetivo da meta {goal.Title} ({id}); preserve os outros campos.", id);
                    }
        }

        private void DayOffers()
        {
            if (read.Block is DaySummaryCard summary && summary.Date == account.Today
                && (summary.DateTo is null || summary.DateTo == summary.Date) && summary.Due == summary.Done && summary.OverdueCount == 0)
                Add("smallStep", summary.Due == 0 ? "Nothing is scheduled today." : "Everything scheduled today is complete.",
                    "Choose one small step", "Escolher um passo pequeno", "Help me choose one small step I can take today.",
                    "Me ajude a escolher um passo pequeno para hoje.", kind: "continueChat", date: account.Today);
            if (read.Block is HabitListCard { Scope: "today", Items.Count: 0, TotalCount: 0 }
                && !read.Read.Scope.TryGetProperty("search", out _) && !read.Read.Scope.TryGetProperty("tag", out _)
                && !read.Read.Scope.TryGetProperty("is_completed", out _))
                Add("smallStep", "The complete unfiltered current-day schedule is empty.", "Choose one small step", "Escolher um passo pequeno",
                    "Help me choose one small step I can take today.", "Me ajude a escolher um passo pequeno para hoje.", kind: "continueChat", date: account.Today);
        }

        private void RecordOffers()
        {
            if (read.Block is not RecordListCard records) return;
            foreach (var record in records.Items)
            {
                if (!Guid.TryParse(record.Id, out var id)) continue;
                if (records.Kind == "notionTasks" && records.Connection?.Connected == true)
                    Add("notion", "A stored Notion task and connection read grounds this page id.", "Plan in Orbit", "Planejar no Orbit",
                        $"Read my stored Notion tasks and prepare an editable Orbit task or habit from page {id}. Preserve its stated date and propose recurrence only if it fits.",
                        $"Leia minhas tarefas salvas do Notion e prepare uma tarefa ou hábito editável no Orbit com a página {id}. Preserve a data e sugira recorrência só se fizer sentido.", id, kind: "continueChat");
                if (records.Kind == "notifications" && record.IsRead == false)
                    Add("notification", "This owned notification is unread.", "Mark as read", "Marcar como lida",
                        $"Prepare a preview to mark notification {id} as read.", $"Prepare uma prévia para marcar a notificação {id} como lida.", id);
                if (records.Kind is "tags" or "templates")
                    Add("reuse", "A stored tag or template can be applied only after resolving an owned habit and full template contents.",
                        "Use with a habit", "Usar em um hábito",
                        $"Read the full {records.Kind} record {id} and my habits, then propose a suitable application in one editable preview.",
                        $"Leia o registro completo de {records.Kind} {id} e meus hábitos, depois sugira uma aplicação adequada em uma prévia editável.", id, kind: "continueChat");
            }
        }

        private void OtherOffers()
        {
            if (account.CalendarAvailable && read.Block is AccountRowsCard { Kind: "profile" } profile
                && profile.Rows.Any(row => row is { Key: "aiSummary", Value: "false" }))
                Add("summary", "Daily summaries are available and disabled in the profile read.", "Review Astra settings", "Ver configurações da Astra",
                    "", "", kind: "openSurface", surface: "ai-settings");
            if (account.CalendarAvailable && !account.CalendarConnected)
                Add("calendar", "Google Calendar is available and disconnected; requires dated_event interpretation.",
                    "Connect Google Calendar", "Conectar Google Calendar", "", "", kind: "openSurface", surface: "calendar-sync");
            Add("aim", "A complete active-goal account snapshot is provided; requires aim interpretation and no equivalent goal.",
                "Plan this goal", "Planejar esta meta", "Help me turn the aim we just discussed into an editable goal preview after checking my existing goals.",
                "Me ajude a transformar o objetivo que acabamos de discutir em uma prévia editável de meta depois de conferir minhas metas existentes.", kind: "continueChat");
            var surface = read.Block switch
            {
                RecordListCard { Kind: "tags" or "templates" } => "today",
                RecordListCard { Kind: "keys" } => "advanced-api",
                RecordListCard { Kind: "notifications" } => "notifications",
                AccountRowsCard { Kind: "referral" } => "referrals",
                AccountRowsCard => "profile-preferences",
                CalendarCard => "calendar-overview",
                _ => "today"
            };
            Add("inspect", "The successful typed read supports inspecting its underlying records or settings.",
                "View details", "Ver detalhes", "", "", kind: "openSurface", surface: surface);
        }
    }

    public static IReadOnlyList<DateOnly> MissedDates(Habit habit, DateOnly today, int weekStartDay,
        IReadOnlyList<HabitScheduleLogDay> days)
    {
        if (habit.Schedule != HabitSchedule.Recurring || habit.IsBadHabit || habit.IsCompleted) return [];
        var resolved = days.Where(day => day.HabitId == habit.Id && (day.CompletedCount > 0 || day.SkippedCount > 0))
            .Select(day => day.Date).ToHashSet();
        return Enumerable.Range(1, 14).Select(offset => today.AddDays(-offset))
            .Where(date => HabitScheduleService.WasScheduledOnDate(habit, date, weekStartDay) && !resolved.Contains(date)).ToArray();
    }
}
