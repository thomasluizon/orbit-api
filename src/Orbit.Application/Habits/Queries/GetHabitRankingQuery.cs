using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Queries;

public record GetHabitRankingQuery(
    Guid UserId,
    DateOnly? DateFrom = null,
    DateOnly? DateToExclusive = null,
    string? Window = null,
    int? Months = null,
    string? Metric = null,
    IReadOnlyList<Guid>? HabitIds = null,
    int? PageSize = null,
    string? QueryToken = null,
    string? Cursor = null,
    bool Refresh = false) : IRequest<Result<HabitRankingResponse>>;

public sealed record HabitRankingScope(
    string EntityType = "positive_recurring_habits",
    bool IncludeChildren = true,
    string ParentCounts = "own_logs_only",
    string InactiveHabits = "include_ended_and_completed",
    string SoftDeletedHabits = "exclude_with_logs",
    string TasksAndSlips = "exclude",
    string ZeroCompletions = "exclude");

public sealed record HabitRankingResolvedQuery(DateOnly DateFrom, DateOnly DateToExclusive,
    string TimeZone, string Metric, string CountDefinition, HabitRankingScope Scope,
    IReadOnlyList<Guid> HabitIds, int PageSize, string BoundaryConvention = "start_inclusive_end_exclusive");

public sealed record HabitRankingRow(int Rank, Guid HabitId, string Name, string? Emoji,
    Guid? ParentHabitId, int CompletedCount);

public sealed record HabitRankingResponse(string QueryId, string QueryToken, string Revision,
    HabitRankingResolvedQuery Query, IReadOnlyList<HabitRankingRow> Rows, int TotalCount,
    int Offset, bool IsComplete, string? NextCursor);

/// <summary>Calendar arithmetic is separate from the legacy retrospective rolling-day periods.</summary>
public static class HabitRankingPeriodRange
{
    public static (DateOnly From, DateOnly ToExclusive) Resolve(string window, int months, DateOnly today)
    {
        if (months is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(months));
        return window switch
        {
            "rolling_months" => (today.AddMonths(-months), today.AddDays(1)),
            "complete_months" => (new DateOnly(today.Year, today.Month, 1).AddMonths(-months),
                new DateOnly(today.Year, today.Month, 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(window))
        };
    }
}

public sealed class GetHabitRankingQueryHandler(
    IGenericRepository<Habit> habits,
    IUserDateService dates,
    IHabitRankingTokenService tokens,
    IProductAnalytics analytics,
    ILogger<GetHabitRankingQueryHandler> logger) : IRequestHandler<GetHabitRankingQuery, Result<HabitRankingResponse>>
{
    private sealed record QueryPayload(string Purpose, Guid UserId, DateOnly DateFrom, DateOnly DateToExclusive,
        string TimeZone, string Metric, Guid[] HabitIds, int PageSize);
    private sealed record CursorPayload(string Purpose, string QueryId, string Revision, int Offset);

    public async Task<Result<HabitRankingResponse>> Handle(GetHabitRankingQuery request, CancellationToken cancellationToken)
    {
        var outcome = await ExecuteAsync(request, cancellationToken);
        AnalyticsCapture.SafeCaptureUserEvent(analytics, logger, request.UserId, null,
            "habit_ranking_query", new Dictionary<string, object>
            {
                ["kind"] = "habitRanking",
                ["metric"] = HabitRankingDefinition.CompletionLogRecords,
                ["operation"] = request.Refresh ? "refresh" : request.Cursor is null ? "query" : "page",
                ["outcome"] = outcome.IsSuccess ? "succeeded" : outcome.ErrorCode ?? "failed",
                ["result_count"] = outcome.IsSuccess ? outcome.Value.Rows.Count : 0,
                ["complete"] = outcome.IsSuccess && outcome.Value.IsComplete
            });
        return outcome;
    }

    private async Task<Result<HabitRankingResponse>> ExecuteAsync(GetHabitRankingQuery request, CancellationToken ct)
    {
        var validation = await new GetHabitRankingQueryValidator().ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Failure("Invalid habit ranking query.");

        var definition = await ResolveAsync(request, ct);
        if (definition.IsFailure)
            return definition.PropagateError<HabitRankingResponse>();
        var query = definition.Value;
        var queryPayload = new QueryPayload("habit-ranking-query-v1", query.UserId, query.DateFrom,
            query.DateToExclusive, query.TimeZone, query.Metric, query.HabitIds.ToArray(), query.PageSize);
        var canonical = JsonSerializer.Serialize(queryPayload);
        var queryId = Hash(canonical);
        CursorPayload? cursor = null;
        if (request.Cursor is not null && !TryReadCursor(request.Cursor, queryId, query.PageSize, out cursor))
            return Failure("Invalid ranking cursor.");

        var eligible = await LoadEligibleAsync(query, ct);
        if (request.QueryToken is null && query.HabitIds.Any(id => eligible.All(h => h.HabitId != id)))
            return Result.Failure<HabitRankingResponse>(ErrorMessages.HabitNotFound);

        var counted = eligible.Where(h => h.CompletedCount > 0)
            .OrderByDescending(h => h.CompletedCount)
            .ThenBy(h => h.HabitId.ToString("D"), StringComparer.Ordinal).ToList();
        var revision = Hash(JsonSerializer.Serialize(new
        {
            QueryId = queryId,
            Rows = counted.Select(h => new
            {
                h.HabitId,
                h.Name,
                h.Emoji,
                h.ParentHabitId,
                h.CompletedCount
            })
        }));
        if (cursor is not null && cursor.Revision != revision)
            return Result.Failure<HabitRankingResponse>("Ranking changed. Refresh the resolved query.", "HABIT_RANKING_STALE");
        var offset = cursor?.Offset ?? 0;
        if (cursor is not null && offset >= counted.Count)
            return Failure("Ranking page is out of range.");
        var rows = counted.Skip(offset).Take(query.PageSize).Select((h, i) => h with { Rank = offset + i + 1 }).ToArray();
        var complete = offset + rows.Length >= counted.Count;
        var next = complete ? null : tokens.Sign(JsonSerializer.Serialize(new CursorPayload(
            "habit-ranking-page-v1", queryId, revision, offset + rows.Length)));
        return Result.Success(new HabitRankingResponse(queryId, tokens.Sign(canonical), revision,
            new HabitRankingResolvedQuery(query.DateFrom, query.DateToExclusive, query.TimeZone, query.Metric,
                "Count of distinct positive completion log records; undone, deleted and slip records excluded.",
                new HabitRankingScope(), query.HabitIds, query.PageSize), rows, counted.Count, offset, complete, next));
    }

    private bool TryReadCursor(string token, string queryId, int pageSize, out CursorPayload? cursor) =>
        TryRead(token, out cursor) && cursor is not null && cursor.Purpose == "habit-ranking-page-v1"
        && cursor.QueryId == queryId && cursor.Offset > 0 && cursor.Offset % pageSize == 0;

    private Task<IReadOnlyList<HabitRankingRow>> LoadEligibleAsync(HabitRankingDefinition query, CancellationToken ct)
    {
        return habits.ProjectAsync(
            h => h.UserId == query.UserId && !h.IsDeleted
                && (h.Schedule == HabitSchedule.Recurring || h.Schedule == HabitSchedule.Flexible) && !h.IsBadHabit
                && (query.HabitIds.Count == 0 || query.HabitIds.Contains(h.Id)),
            q => q.SelectMany(h => h.Logs.Where(l => !l.IsDeleted && !l.IsSlip && l.Value > 0
                    && l.Date >= query.DateFrom && l.Date < query.DateToExclusive).DefaultIfEmpty(),
                (h, log) => new { h.Id, h.Title, h.Emoji, h.ParentHabitId, LogId = log == null ? (Guid?)null : log.Id })
                .GroupBy(row => new { row.Id, row.Title, row.Emoji, row.ParentHabitId })
                .Select(group => new HabitRankingRow(0, group.Key.Id, group.Key.Title, group.Key.Emoji,
                    group.Key.ParentHabitId, group.Select(row => row.LogId).Distinct().Count(id => id != null))), ct);
    }

    private async Task<Result<HabitRankingDefinition>> ResolveAsync(GetHabitRankingQuery request, CancellationToken ct)
    {
        if (request.QueryToken is not null)
        {
            if (!TryRead<QueryPayload>(request.QueryToken, out var payload) || payload is null
                || payload.Purpose != "habit-ranking-query-v1" || payload.UserId != request.UserId)
                return Result.Failure<HabitRankingDefinition>("Invalid ranking query token.", "HABIT_RANKING_INVALID");
            return HabitRankingDefinition.Create(payload.UserId, payload.DateFrom, payload.DateToExclusive,
                payload.TimeZone, payload.Metric, payload.HabitIds, payload.PageSize);
        }
        var timeZone = await dates.GetUserTimeZoneAsync(request.UserId, ct);
        var from = request.DateFrom;
        var to = request.DateToExclusive;
        if (request.Window is not null)
        {
            var today = await dates.GetUserTodayAsync(request.UserId, ct);
            try
            {
                (from, to) = HabitRankingPeriodRange.Resolve(request.Window, request.Months!.Value, today);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Result.Failure<HabitRankingDefinition>("Invalid ranking calendar window.", "HABIT_RANKING_INVALID");
            }
        }
        return HabitRankingDefinition.Create(request.UserId, from!.Value, to!.Value,
            timeZone.Id, request.Metric ?? HabitRankingDefinition.CompletionLogRecords,
            request.HabitIds, request.PageSize ?? 20);
    }

    private bool TryRead<T>(string token, out T? payload)
    {
        payload = default;
        if (!tokens.TryRead(token, out var json))
            return false;
        try
        {
            payload = JsonSerializer.Deserialize<T>(json);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Result<HabitRankingResponse> Failure(string message) =>
        Result.Failure<HabitRankingResponse>(message, "HABIT_RANKING_INVALID");
}
