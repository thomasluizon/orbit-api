using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class ConversationTurnRecorder(
    IServiceScopeFactory scopeFactory,
    IOptions<AiSettings> aiSettings,
    ILogger<ConversationTurnRecorder> logger) : BackgroundService, IConversationTurnRecorder
{
    public const int Capacity = 64;
    private const int BatchSize = 16;
    private readonly Channel<ConversationTurnCapture> _channel = Channel.CreateBounded<ConversationTurnCapture>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _drainCancellation = new();
    private readonly ConversationTurnCaptureValidator _validator = new();
    private int _disposed;

    public bool TryRecord(ConversationTurnCapture turn)
    {
        if (!_validator.Validate(turn).IsValid)
        {
            logger.LogWarning("Conversation recording dropped an invalid or oversized turn");
            return false;
        }
        if (_channel.Writer.TryWrite(turn))
            return true;
        logger.LogWarning("Conversation recording queue is full or closed; turn dropped");
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_drainCancellation.Token))
            {
                var batch = new List<ConversationTurnCapture>(BatchSize);
                while (batch.Count < BatchSize && _channel.Reader.TryRead(out var turn))
                    batch.Add(turn);
                try
                {
                    await WriteBatchAsync(batch, _drainCancellation.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !_drainCancellation.IsCancellationRequested)
                {
                    logger.LogWarning("Conversation batch persistence failed with {FailureType}; {TurnCount} turns dropped",
                        exception.GetType().Name, batch.Count);
                }
            }
        }
        catch (OperationCanceledException) when (_drainCancellation.IsCancellationRequested)
        {
            logger.LogWarning("Conversation recording shutdown deadline reached; remaining turns dropped");
        }
    }

    private async Task WriteBatchAsync(List<ConversationTurnCapture> batch, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        if (!encryption.IsConfigured || !db.HasEncryptionService)
        {
            logger.LogWarning("Conversation recording requires configured field encryption; batch dropped");
            return;
        }
        var flags = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await work.ExecuteInTransactionAsync(async token =>
        {
            foreach (var group in batch.GroupBy(turn => turn.UserId).OrderBy(group => group.Key))
            {
                await work.AcquireUserRowLockAsync(group.Key, token);
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == group.Key, token);
                if (user is null || user.IsDeactivated)
                    continue;
                var enabled = await flags.GetEnabledKeysForUserAsync(group.Key, token);
                if (!enabled.Contains(FeatureFlagKeys.AstraConversationRecording))
                    continue;
                var cutoffAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-ConversationTurn.RetentionDays);
                foreach (var turn in group.Where(turn => turn.CreatedAtUtc >= cutoffAtUtc
                    && (user.ConversationHistoryClearedAtUtc is null || turn.CreatedAtUtc > user.ConversationHistoryClearedAtUtc)))
                {
                    var userTurn = ConversationTurn.Create(turn.UserId, turn.TurnId, "user", turn.CreatedAtUtc,
                        null, turn.UserContent, "[]");
                    var assistantTurn = ConversationTurn.Create(turn.UserId, turn.TurnId, "assistant", turn.CreatedAtUtc,
                        turn.UsedModel ? aiSettings.Value.Model : null, turn.AssistantContent, turn.ToolCallsJson, turn.ResponseJson);
                    if (userTurn.IsFailure || assistantTurn.IsFailure)
                    {
                        logger.LogWarning("Conversation entity validation failed; turn dropped");
                        continue;
                    }
                    db.ConversationTurns.AddRange(userTurn.Value, assistantTurn.Value);
                }
            }
            await work.SaveChangesAsync(token);
        }, cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (ExecuteTask is not null)
                await ExecuteTask.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            await _drainCancellation.CancelAsync();
        }
        await base.StopAsync(deadline.Token);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _channel.Writer.TryComplete();
        _drainCancellation.Cancel();
        base.Dispose();
        _drainCancellation.Dispose();
    }
}
