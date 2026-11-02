using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Connections;
using Orbit.Application.Goals.Services;
using Orbit.Application.Profile.Models;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Queries;

public record StreamUserDataQuery(Guid UserId) : IRequest<Result<StreamedUserData>>;

public sealed record StreamedUserData(Func<Stream, CancellationToken, Task> WriteAsync);

public sealed class StreamUserDataQueryValidator : AbstractValidator<StreamUserDataQuery>
{
    public StreamUserDataQueryValidator() => RuleFor(query => query.UserId).NotEmpty();
}

public sealed class StreamUserDataQueryHandler(
    ExportUserDataRepositories repos,
    IUserDateService dates,
    IGoalProgressReadSyncer progress) : IRequestHandler<StreamUserDataQuery, Result<StreamedUserData>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int ParentBatchSize = 100;

    public async Task<Result<StreamedUserData>> Handle(StreamUserDataQuery request, CancellationToken cancellationToken)
    {
        var user = await repos.Users.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null) return Result.Failure<StreamedUserData>(ErrorMessages.UserNotFound);
        var today = await dates.GetUserTodayAsync(user.Id, cancellationToken);
        var fresh = await progress.ComputeFreshValuesAsync(user.Id, today, cancellationToken);
        var exportedAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        return Result.Success(new StreamedUserData((destination, ct) => WriteAsync(destination, user, fresh, exportedAtUtc, ct)));
    }

    private async Task WriteAsync(Stream destination, User user, IReadOnlyDictionary<Guid, int> fresh,
        DateTime exportedAtUtc, CancellationToken ct)
    {
        await using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        Write(writer, "exportedAtUtc", exportedAtUtc);
        Write(writer, "account", new ExportedAccount(user.Name, user.Email, user.CreatedAtUtc, user.HasProAccess ? "pro" : "free"));
        Write(writer, "settings", new ExportedSettings(user.TimeZone, user.Language, user.WeekStartDay,
            user.ThemePreference, user.ColorScheme, user.AiSummaryEnabled, user.EffectiveProactivePushEnabled,
            user.Uses24HourClockPreference, user.ProactiveAstraEnabled));
        Write(writer, "subscription", new ExportedSubscription(user.Plan.ToString(), user.IsLifetimePro,
            user.SubscriptionSource?.ToString(), user.SubscriptionInterval?.ToString(), user.PlanExpiresAt, user.TrialEndsAt));
        await WriteHabitsAsync(writer, user.Id, ct);
        await WriteGoalsAsync(writer, user.Id, fresh, ct);
        await WriteRows(writer, "tags", repos.Tags.StreamAsync(t => t.UserId == user.Id,
            q => q.Select(t => new ExportedTag(t.Id, t.Name, t.Color, t.CreatedAtUtc)), ct), ct);
        await WriteRows(writer, "notifications", repos.Notifications.StreamAsync(n => n.UserId == user.Id,
            q => q.OrderBy(n => n.CreatedAtUtc).ThenBy(n => n.Id).Select(n => new ExportedNotification(n.Id, n.Title, n.Body, n.Url, n.IsRead, n.CreatedAtUtc)), ct), ct);
        await WriteRows(writer, "checklistTemplates", repos.ChecklistTemplates.StreamAsync(t => t.UserId == user.Id,
            q => q.OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.Id).Select(t => new ExportedChecklistTemplate(t.Id, t.Name, t.Items, t.CreatedAtUtc)), ct), ct);
        await WriteRows(writer, "achievements", repos.UserAchievements.StreamAsync(a => a.UserId == user.Id,
            q => q.OrderBy(a => a.EarnedAtUtc).ThenBy(a => a.Id).Select(a => new ExportedAchievement(a.AchievementId, a.EarnedAtUtc)), ct), ct);
        await WriteRows(writer, "streakFreezes", repos.StreakFreezes.StreamAsync(s => s.UserId == user.Id,
            q => q.OrderBy(s => s.UsedOnDate).ThenBy(s => s.Id).Select(s => new ExportedStreakFreeze(s.UsedOnDate, s.CreatedAtUtc)), ct), ct);
        await WriteRows(writer, "referrals", repos.Referrals.StreamAsync(r => r.ReferrerId == user.Id,
            q => q.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id).Select(r => new ExportedReferral(r.Status.ToString(), r.CreatedAtUtc, r.CompletedAtUtc, r.RewardGrantedAtUtc)), ct), ct);
        await WriteRows(writer, "apiKeys", repos.ApiKeys.StreamAsync(k => k.UserId == user.Id,
            q => q.OrderBy(k => k.CreatedAtUtc).ThenBy(k => k.Id).Select(k => ExportUserDataQueryHandler.MapApiKey(k)), ct), ct);
        foreach (var retired in new[] { "friendships", "cheers", "blockedUsers", "reports", "friendFeedEvents" })
        {
            writer.WriteStartArray(retired);
            writer.WriteEndArray();
        }
        await WriteRows(writer, "proactiveItems", repos.ProactiveItems.StreamAsync(i => i.UserId == user.Id,
            q => q.OrderBy(i => i.CreatedAtUtc).ThenBy(i => i.Id).Select(i => new ExportedProactiveItem(i.Id, i.Trigger.ToString(), i.LocalDate,
                i.Text, i.ActionLabel, i.ActionPrompt, i.CreatedAtUtc, i.ShownAtUtc, i.PushedAtUtc, i.OpenedAtUtc,
                i.ActedAtUtc, i.EndedAtUtc, i.EndReason == null ? null : i.EndReason.ToString())), ct), ct);
        var connections = await repos.NotionConnections.ProjectAsync(c => c.UserId == user.Id,
            q => q.Select(c => new ExportedNotionConnection(c.WorkspaceName, c.Status.ToString(), c.CreatedAtUtc, c.LastPollAtUtc)), ct);
        Write(writer, "notionConnection", connections.SingleOrDefault());
        await WriteRows(writer, "notionTasks", repos.NotionTasks.StreamAsync(t => t.UserId == user.Id,
            q => q.Select(t => NotionTaskResponse.FromEntity(t)), ct), ct);
        await WriteRows(writer, "conversations", repos.ConversationTurns.StreamAsync(t => t.UserId == user.Id,
            q => q.OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.Role == "user" ? 0 : 1).ThenBy(t => t.Id)
                .Select(t => MapConversation(t)), ct), ct);
        writer.WriteStartArray("conversationOffers");
        if (repos.ConversationOffers is { } offers)
            await foreach (var offer in offers.StreamAsync(o => o.UserId == user.Id,
                q => q.Select(o => new ExportedConversationOffer(o.ConversationId, o.OfferKey, o.Dismissed, o.DefinitionJson)), ct))
            {
                JsonSerializer.Serialize(writer, offer, JsonOptions);
                await writer.FlushAsync(ct);
            }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    private async Task WriteHabitsAsync(Utf8JsonWriter writer, Guid userId, CancellationToken ct)
    {
        writer.WriteStartArray("habits");
        Guid? cursor = null;
        while (true)
        {
            var batch = await repos.Habits.ProjectAsync(h => h.UserId == userId && (cursor == null || h.Id.CompareTo(cursor.Value) > 0),
                q => q.OrderBy(h => h.Id).Take(ParentBatchSize), ct);
            if (batch.Count == 0) break;
            foreach (var habit in batch)
            {
                var row = ExportUserDataQueryHandler.MapHabit(habit, []);
                WriteWithoutArray(writer, row, "logs");
                await WriteRows(writer, "logs", repos.HabitLogs.StreamAsync(l => l.HabitId == habit.Id,
                    q => q.OrderBy(l => l.Date).ThenBy(l => l.Id).Select(l => new ExportedHabitLog(l.Date, l.Value, l.Note, l.CreatedAtUtc)), ct), ct);
                writer.WriteEndObject();
            }
            cursor = batch[^1].Id;
        }
        writer.WriteEndArray();
    }

    private async Task WriteGoalsAsync(Utf8JsonWriter writer, Guid userId, IReadOnlyDictionary<Guid, int> fresh, CancellationToken ct)
    {
        writer.WriteStartArray("goals");
        Guid? cursor = null;
        while (true)
        {
            var batch = await repos.Goals.ProjectAsync(g => g.UserId == userId && (cursor == null || g.Id.CompareTo(cursor.Value) > 0),
                q => q.OrderBy(g => g.Id).Take(ParentBatchSize), ct);
            if (batch.Count == 0) break;
            foreach (var goal in batch)
            {
                WriteWithoutArray(writer, ExportUserDataQueryHandler.MapGoal(goal, [], fresh), "progressLogs");
                await WriteRows(writer, "progressLogs", repos.GoalProgressLogs.StreamAsync(l => l.GoalId == goal.Id,
                    q => q.OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id).Select(l => new ExportedGoalProgressLog(l.Value, l.PreviousValue, l.Note, l.CreatedAtUtc)), ct), ct);
                writer.WriteEndObject();
            }
            cursor = batch[^1].Id;
        }
        writer.WriteEndArray();
    }

    private static void WriteWithoutArray<T>(Utf8JsonWriter writer, T value, string array)
    {
        writer.WriteStartObject();
        foreach (var property in JsonSerializer.SerializeToElement(value, JsonOptions).EnumerateObject())
            if (property.Name != array) property.WriteTo(writer);
    }

    private static void Write<T>(Utf8JsonWriter writer, string name, T value)
    {
        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, value, JsonOptions);
    }

    private static async Task WriteRows<T>(Utf8JsonWriter writer, string name, IAsyncEnumerable<T> rows, CancellationToken ct)
    {
        writer.WriteStartArray(name);
        await foreach (var row in rows.WithCancellation(ct))
        {
            JsonSerializer.Serialize(writer, row, JsonOptions);
            await writer.FlushAsync(ct);
        }
        writer.WriteEndArray();
    }

    private static ExportedConversationTurn MapConversation(ConversationTurn turn) => new(turn.TurnId, turn.Role,
        turn.CreatedAtUtc, turn.Model, turn.Content,
        JsonSerializer.Deserialize<Orbit.Application.Chat.ConversationToolCall[]>(turn.ToolCallsJson) ?? [],
        turn.ResponseJson, turn.ConversationId, turn.TurnOrder, turn.ToolResultsJson, turn.PreviewOutcomesJson,
        turn.ImageObjectKey is null ? null : Path.GetFileName(turn.ImageObjectKey));
}
