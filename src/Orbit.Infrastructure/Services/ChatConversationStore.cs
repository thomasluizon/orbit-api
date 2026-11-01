using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class ChatConversationStore(OrbitDbContext db) : IChatConversationStore
{
    private sealed record StoredPreview(PendingAgentOperation Proposal, string? Arguments);
    private sealed record StoredTurn(string UserMessage, string? AssistantMessage,
        AiConversationAttachment? Attachment, IReadOnlyList<StoredPreview> Previews);

    public async Task SaveAsync(ChatConversationCapture capture, CancellationToken ct)
    {
        await new ChatConversationCaptureValidator().ValidateAndThrowAsync(capture, ct);
        if (await db.ChatConversationTurns.AnyAsync(turn => turn.UserId == capture.UserId
            && turn.ConversationId == capture.ConversationId && turn.TurnId == capture.TurnId, ct)) return;
        var ids = capture.Previews.Select(preview => preview.Id).ToArray();
        var arguments = await db.PendingAgentOperations.AsNoTracking()
            .Where(operation => operation.UserId == capture.UserId && ids.Contains(operation.Id))
            .ToDictionaryAsync(operation => operation.Id, operation => operation.ArgumentsJson, ct);
        var stored = new StoredTurn(capture.UserMessage, capture.AssistantMessage,
            capture.ImageData is null ? null : new(capture.UserMessage, capture.ImageData, capture.ImageMimeType!),
            capture.Previews.Select(preview => new StoredPreview(preview, arguments.GetValueOrDefault(preview.Id))).ToArray());
        var entity = ChatConversationTurn.Create(capture.UserId, capture.ConversationId, capture.TurnId,
            capture.CreatedAtUtc, JsonSerializer.Serialize(stored), stored.Attachment is not null, stored.Previews.Count > 0);
        if (entity.IsFailure) throw new InvalidOperationException(entity.Error);
        db.ChatConversationTurns.Add(entity.Value);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ChatConversationMemory> LoadAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var cutoffAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-ConversationTurn.RetentionDays);
        var query = db.ChatConversationTurns.AsNoTracking().Where(turn => turn.UserId == userId
            && turn.ConversationId == conversationId && turn.CreatedAtUtc >= cutoffAtUtc);
        var recent = await query.OrderByDescending(turn => turn.CreatedAtUtc).ThenByDescending(turn => turn.Id)
            .Take(20).ToListAsync(ct);
        var imageTurn = await query.Where(turn => turn.HasAttachment).OrderByDescending(turn => turn.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        var previewTurn = await query.Where(turn => turn.HasPreview).OrderByDescending(turn => turn.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        var history = recent.AsEnumerable().Reverse().Select(turn => JsonSerializer.Deserialize<StoredTurn>(turn.DataJson)!)
            .SelectMany(turn => string.IsNullOrWhiteSpace(turn.AssistantMessage)
                ? new[] { new ChatHistoryMessage("user", turn.UserMessage) }
                : [new ChatHistoryMessage("user", turn.UserMessage), new ChatHistoryMessage("assistant", turn.AssistantMessage)])
            .TakeLast(20).ToArray();
        var attachment = imageTurn is null ? null : JsonSerializer.Deserialize<StoredTurn>(imageTurn.DataJson)?.Attachment;
        var proposed = previewTurn is null ? [] : JsonSerializer.Deserialize<StoredTurn>(previewTurn.DataJson)!.Previews;
        var ids = proposed.Select(preview => preview.Proposal.Id).ToArray();
        var states = await db.PendingAgentOperations.AsNoTracking()
            .Where(operation => operation.UserId == userId && ids.Contains(operation.Id))
            .ToDictionaryAsync(operation => operation.Id, ct);
        var nowAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var previews = proposed.Select(preview =>
        {
            var state = states.GetValueOrDefault(preview.Proposal.Id);
            var edited = state is not null && state.ArgumentsJson != preview.Arguments;
            var outcome = state is null ? "unavailable"
                : state.ConsumedAtUtc.HasValue ? state.ConfirmationTokenHash is null ? "rejected" : "approved"
                : state.ConfirmedAtUtc.HasValue ? "approved"
                : state.IsExpired(nowAtUtc) ? "expired" : edited ? "edited" : "pending";
            return new ChatConversationPreview(preview.Proposal, outcome, edited,
                state is null ? null : JsonSerializer.Deserialize<JsonElement>(state.ArgumentsJson));
        }).ToArray();
        return new(history, attachment is null ? [] : [attachment], previews);
    }
}
