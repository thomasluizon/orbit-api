using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Polls in-flight OpenAI fact-extraction batches each tick. Completed batches have their output
/// downloaded, parsed, and persisted as <see cref="UserFact"/>s (with the same dedup and cap the
/// inline path used); failed/expired/cancelled batches are marked failed. Hosted-loop and durable
/// Hangfire execution share one tick via <see cref="RunAsync"/>, mirroring the other schedulers.
/// </summary>
public sealed partial class OpenAiBatchPollerService(
    IServiceScopeFactory scopeFactory,
    ILogger<OpenAiBatchPollerService> logger,
    IConfiguration configuration) : ScheduledServiceBase, IScheduledJob
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:BatchPollIntervalMinutes", 2));

    public string Name => "openai-batch-poller";

    public string CronExpression => "*/2 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        await PollPendingBatches(stoppingToken);
        BackgroundServiceHealthCheck.RecordTick("OpenAiBatchPoller");
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task PollPendingBatches(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var batchClient = scope.ServiceProvider.GetRequiredService<IAiBatchClient>();
        var batchRepository = scope.ServiceProvider.GetRequiredService<IGenericRepository<AiFactExtractionBatch>>();
        var userFactRepository = scope.ServiceProvider.GetRequiredService<IGenericRepository<UserFact>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var appConfig = scope.ServiceProvider.GetRequiredService<IAppConfigService>();

        var pending = await batchRepository.FindTrackedAsync(
            b => b.Status == AiFactExtractionBatchStatus.Submitted, ct);

        foreach (var batch in pending)
            await ProcessBatchAsync(batch, batchClient, userFactRepository, unitOfWork, appConfig, ct);
    }

    private async Task ProcessBatchAsync(
        AiFactExtractionBatch batch,
        IAiBatchClient batchClient,
        IGenericRepository<UserFact> userFactRepository,
        IUnitOfWork unitOfWork,
        IAppConfigService appConfig,
        CancellationToken ct)
    {
        try
        {
            var status = await batchClient.GetBatchAsync(batch.BatchId, ct);

            if (status.Status == "completed" && !string.IsNullOrWhiteSpace(status.OutputFileId))
                await CompleteBatchAsync(batch, status.OutputFileId, batchClient, userFactRepository, unitOfWork, appConfig, ct);
            else if (IsTerminalFailure(status.Status))
                await FailBatchAsync(batch, status, batchClient, unitOfWork, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBatchPollFailed(logger, batch.BatchId, ex);
        }
    }

    private async Task CompleteBatchAsync(
        AiFactExtractionBatch batch,
        string outputFileId,
        IAiBatchClient batchClient,
        IGenericRepository<UserFact> userFactRepository,
        IUnitOfWork unitOfWork,
        IAppConfigService appConfig,
        CancellationToken ct)
    {
        var outputJsonl = await batchClient.DownloadFileAsync(outputFileId, ct);
        var facts = ParseExtractedFacts(outputJsonl);

        await PersistFactsAsync(batch.UserId, facts, userFactRepository, appConfig, ct);

        batch.MarkCompleted(outputFileId);
        await unitOfWork.SaveChangesAsync(ct);

        await DeleteFilesAsync(batchClient, batch.InputFileId, outputFileId, ct);

        if (logger.IsEnabled(LogLevel.Information))
            LogBatchCompleted(logger, batch.BatchId, facts.Count);
    }

    private async Task FailBatchAsync(
        AiFactExtractionBatch batch,
        BatchStatusResult status,
        IAiBatchClient batchClient,
        IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        batch.MarkFailed();
        await unitOfWork.SaveChangesAsync(ct);

        await DeleteFilesAsync(batchClient, batch.InputFileId, status.OutputFileId ?? status.ErrorFileId, ct);

        LogBatchFailed(logger, batch.BatchId, status.Status);
    }

    private async Task PersistFactsAsync(
        Guid userId,
        IReadOnlyList<FactCandidate> candidates,
        IGenericRepository<UserFact> userFactRepository,
        IAppConfigService appConfig,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return;

        var existingFacts = await userFactRepository.FindAsync(f => f.UserId == userId && !f.IsDeleted, ct);
        var existingFactCount = existingFacts.Count;

        var maxFacts = await appConfig.GetAsync(AppConfigKeys.MaxUserFacts, AppConstants.MaxUserFacts, ct);
        if (existingFactCount >= maxFacts)
            return;

        var existingTexts = existingFacts
            .Select(f => f.FactText.Trim().ToLowerInvariant())
            .ToHashSet();

        var remaining = maxFacts - existingFactCount;
        foreach (var candidate in candidates)
        {
            if (remaining <= 0)
                break;
            if (!existingTexts.Add(candidate.FactText.Trim().ToLowerInvariant()))
                continue;

            var factResult = UserFact.Create(userId, candidate.FactText, candidate.Category);
            if (factResult.IsSuccess)
            {
                await userFactRepository.AddAsync(factResult.Value, ct);
                remaining--;
            }
        }
    }

    internal static IReadOnlyList<FactCandidate> ParseExtractedFacts(string outputJsonl)
    {
        var line = outputJsonl
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line))
            return [];

        var content = ExtractMessageContent(line);
        if (string.IsNullOrWhiteSpace(content))
            return [];

        try
        {
            var extracted = JsonSerializer.Deserialize<ExtractedFacts>(content, JsonOptions);
            return extracted?.Facts ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ExtractMessageContent(string jsonlLine)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonlLine);
            return document.RootElement
                .GetProperty("response")
                .GetProperty("body")
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static async Task DeleteFilesAsync(IAiBatchClient batchClient, string? inputFileId, string? resultFileId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(inputFileId))
            await batchClient.DeleteFileAsync(inputFileId, ct);
        if (!string.IsNullOrWhiteSpace(resultFileId))
            await batchClient.DeleteFileAsync(resultFileId, ct);
    }

    private static bool IsTerminalFailure(string status)
        => status is "failed" or "expired" or "cancelled";

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "OpenAiBatchPollerService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "OpenAiBatchPollerService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in OpenAI batch poller")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Fact-extraction batch {BatchId} completed; persisted up to {FactCount} facts")]
    private static partial void LogBatchCompleted(ILogger logger, string batchId, int factCount);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Fact-extraction batch {BatchId} ended in terminal status {Status}")]
    private static partial void LogBatchFailed(ILogger logger, string batchId, string status);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Polling fact-extraction batch {BatchId} failed - will retry next tick")]
    private static partial void LogBatchPollFailed(ILogger logger, string batchId, Exception ex);
}
