using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Connections;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services.Notion;

internal sealed class NotionPollService(OrbitDbContext db, NotionAccessGate gate, INotionApi api,
    IUserDateService dates, IUnitOfWork unitOfWork, TimeProvider clock,
    IProductAnalytics analytics, ILogger<NotionPollService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PollAsync(Guid userId, CancellationToken ct)
    {
        if ((await gate.CheckAsync(userId, ct)).IsFailure) return;
        var connection = await db.NotionConnections.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (connection is null || connection.Status == NotionConnectionStatus.ReconnectRequired) return;
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        if (connection.LastPollAtUtc > nowUtc.AddMinutes(-15)) return;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.HasProAccess) return;
        var today = await dates.GetUserTodayAsync(userId, ct);
        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, userId);
        var cutoff = nowUtc.AddMinutes(-15);
        var claimed = await db.NotionConnections.Where(c => c.Id == connection.Id
            && c.Status != NotionConnectionStatus.ReconnectRequired
            && (c.LastPollAtUtc == null || c.LastPollAtUtc <= cutoff))
            .ExecuteUpdateAsync(update => update.SetProperty(c => c.LastPollAtUtc, nowUtc), ct);
        if (claimed != 1) return;

        var tasks = new List<NotionTask>();
        var read = new PollReadState();
        IReadOnlyList<NotionDatabase> databases = [];
        int? failureCode = null;
        try
        {
            var sources = await api.SearchAsync(connection.AccessToken, ct);
            var mappers = sources.Select(s => new NotionTaskMapper(s)).ToArray();
            var shared = new List<NotionDatabase>();
            foreach (var databaseId in mappers.Select(m => m.Database.Id).Distinct())
            {
                var database = await api.GetDatabaseAsync(connection.AccessToken, databaseId, ct);
                shared.Add(new NotionDatabase(databaseId, NotionTaskMapper.ReadTitle(database.GetProperty("title"))));
            }
            databases = shared;
            var pageIds = new HashSet<Guid>();
            foreach (var mapper in mappers)
            {
                if (tasks.Count == 100) { read.Truncated = true; break; }
                await ReadSourceAsync(mapper, connection.AccessToken,
                    new PollReadContext(userId, today, userTimeZone, nowUtc), tasks, pageIds, read, ct);
            }
        }
        catch (NotionPaginationTruncatedException exception)
        {
            failureCode = 0;
            read.Truncated = true;
            read.InputCount += exception.InputCount;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failureCode = ex is NotionApiException provider ? provider.StatusCode : 0;
            logger.LogWarning("Notion poll failed for {UserId} with status {StatusCode}", userId, failureCode);
            analytics.CaptureUserEvent(userId, "notion_poll_failed", "pro", new Dictionary<string, object> { ["status_code"] = failureCode.Value });
        }

        if (read.Truncated)
            logger.LogWarning("Notion poll truncated for {UserId} after {InputCount} input rows and {MappedCount} mapped tasks; unseen tasks are retained",
                userId, read.InputCount, tasks.Count);

        await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            await unitOfWork.AcquireUserRowLockAsync(userId, transactionToken);
            if ((await gate.CheckAsync(userId, transactionToken)).IsFailure) return;
            var current = await db.NotionConnections.SingleOrDefaultAsync(
                c => c.Id == connection.Id && c.LastPollAtUtc == nowUtc, transactionToken);
            if (current is null || current.Status == NotionConnectionStatus.ReconnectRequired) return;
            if (failureCode is not null) current.FailPoll(failureCode == 401, nowUtc);
            else
            {
                var reached = read.ReachedPages.ToArray();
                await db.NotionTasks.Where(t => t.UserId == userId && (!read.Truncated || reached.Contains(t.PageId)))
                    .ExecuteDeleteAsync(transactionToken);
                db.NotionTasks.AddRange(tasks);
                current.CompletePoll(JsonSerializer.Serialize(databases, JsonOptions), nowUtc);
            }
            await db.SaveChangesAsync(transactionToken);
        }, ct);
    }
    private sealed record PollReadContext(Guid UserId, DateOnly Today, TimeZoneInfo Zone, DateTime NowAtUtc);

    private async Task ReadSourceAsync(NotionTaskMapper mapper, string token, PollReadContext context,
        List<NotionTask> tasks, HashSet<Guid> pageIds, PollReadState read, CancellationToken ct)
    {
        var inputCount = 0;
        try
        {
            await foreach (var page in api.QueryAsync(token, mapper.DataSourceId, ct))
            {
                inputCount++;
                read.InputCount++;
                read.ReachedPages.Add(page.GetProperty("id").GetGuid());
                var task = mapper.Map(page, context.UserId, context.Today, context.Zone, context.NowAtUtc);
                if (task is not null && pageIds.Add(task.PageId)) tasks.Add(task);
                if (tasks.Count == 100 || inputCount >= 500) { read.Truncated = true; break; }
            }
        }
        catch (NotionPaginationTruncatedException)
        {
            read.Truncated = true;
        }
    }

    private sealed class PollReadState
    {
        internal bool Truncated { get; set; }
        internal int InputCount { get; set; }
        internal HashSet<Guid> ReachedPages { get; } = [];
    }
}
