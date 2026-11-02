using System.Security.Cryptography;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class ConversationTurnRecorder(
    OrbitDbContext db,
    IUnitOfWork work,
    IFeatureFlagService flags,
    IEncryptionService encryption,
    IObjectStorageService storage,
    IOptions<AiSettings> aiSettings) : IConversationTurnRecorder
{
    public const int HistoryRows = 10;
    public const int ImageHistoryRows = 6;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<bool> RecordAsync(ConversationTurnCapture turn, CancellationToken cancellationToken = default)
    {
        return await work.ExecuteInTransactionAsync(async token =>
        {
            await work.AcquireUserRowLockAsync(turn.UserId, token);
            if (!(await flags.GetEnabledKeysForUserAsync(turn.UserId, token)).Contains(FeatureFlagKeys.AstraConversationRecording))
                return false;
            await new ConversationTurnCaptureValidator().ValidateAndThrowAsync(turn, token);
            if (!encryption.IsConfigured || !db.HasEncryptionService)
                throw new InvalidOperationException("Conversation storage requires configured field encryption.");
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == turn.UserId, token);
            if (!CanRecord(user, turn.CreatedAtUtc))
                return false;
            var conversationId = turn.ConversationId ?? turn.TurnId;
            var query = db.ConversationTurns.Where(row => row.UserId == turn.UserId && row.ConversationId == conversationId);
            if (turn.ExistingConversation && !await query.AnyAsync(token))
                return false;
            if (await db.ConversationTurns.AnyAsync(row => row.ConversationId == conversationId && row.UserId != turn.UserId, token))
                throw new InvalidOperationException("Conversation is unavailable.");
            if (await query.AnyAsync(row => row.TurnId == turn.TurnId, token))
                return true;
            var order = (await query.MaxAsync(row => row.TurnOrder, token) ?? 0) + 1;
            var (imageKey, imageSha256) = ImageMetadata(turn, conversationId);
            var resultsJson = JsonSerializer.Serialize(turn.ToolResults ?? [], JsonOptions);
            var summaries = resultsJson.Length <= 12_000 ? resultsJson : "[" + ModelToolPayload.Fit(resultsJson, 12_000).ToJsonString() + "]";
            var previews = await db.PendingAgentOperations.AsNoTracking().Where(item => item.UserId == turn.UserId
                && item.ConversationId == conversationId && item.CreatedAtUtc == turn.CreatedAtUtc
                && item.PreviewFingerprint != null).OrderBy(item => item.Id).ToListAsync(token);
            var outcomes = JsonSerializer.Serialize(previews.Select(Preview));
            var userRow = ConversationTurn.Create(turn.UserId, turn.TurnId, "user", turn.CreatedAtUtc, null,
                turn.UserContent, "[]", conversationId: conversationId, turnOrder: order,
                imageObjectKey: imageKey, imageContentType: imageKey is null ? null : turn.ImageMimeType, imageSha256: imageSha256);
            var assistantRow = ConversationTurn.Create(turn.UserId, turn.TurnId, "assistant", turn.CreatedAtUtc,
                turn.UsedModel ? aiSettings.Value.Model : null, turn.AssistantContent, turn.ToolCallsJson, turn.ResponseJson,
                conversationId, order + 1, summaries, outcomes);
            if (userRow.IsFailure || assistantRow.IsFailure)
                throw new InvalidOperationException(userRow.IsFailure ? userRow.Error : assistantRow.Error);
            try
            {
                if (imageKey is not null)
                    await storage.PutPrivateAsync(imageKey, turn.ImageMimeType!, turn.ImageData!, token);
                db.ConversationTurns.AddRange(userRow.Value, assistantRow.Value);
                await work.SaveChangesAsync(token);
            }
            catch
            {
                if (imageKey is not null)
                    await storage.DeletePrivateAsync(imageKey, CancellationToken.None);
                throw;
            }
            return true;
        }, cancellationToken);
    }

    private static bool CanRecord(User? user, DateTime startedAtUtc) =>
        user is { IsDeactivated: false } && !(user.ConversationHistoryClearedAtUtc >= startedAtUtc);

    private static (string? Key, string? Sha256) ImageMetadata(ConversationTurnCapture turn, Guid conversationId)
    {
        if (turn.ImageData is not { Length: > 0 }) return (null, null);
        var extension = turn.ImageMimeType switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            _ => throw new ArgumentException("Conversation image type is invalid.")
        };
        return ($"{turn.UserId}/conversations/{conversationId}/{turn.TurnId}.{extension}",
            Convert.ToHexStringLower(SHA256.HashData(turn.ImageData)));
    }

    internal static ConversationPreviewOutcome Preview(PendingAgentOperationState state)
    {
        var outcome = state.ConsumedAtUtc.HasValue ? state.ConfirmationTokenHash is null ? "rejected" : "approved"
            : state.ConfirmedAtUtc.HasValue ? "approved"
            : state.IsExpired(TimeProvider.System.GetUtcNow().UtcDateTime) ? "expired"
            : state.RevisionNumber > 1 ? "edited" : "pending";
        return new(state.Id, outcome, JsonSerializer.SerializeToElement(ModelToolPayload.Fit(state.ArgumentsJson, 8_000)), state.RevisionNumber);
    }

    public async Task<Result<StoredConversationHistory>> LoadHistoryAsync(Guid userId, Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (await db.ConversationTurns.AsNoTracking().AnyAsync(row => row.ConversationId == conversationId && row.UserId != userId, cancellationToken))
            return Result.Failure<StoredConversationHistory>(ConversationErrors.NotFound);
        var rows = await db.ConversationTurns.AsNoTracking().Where(row => row.UserId == userId && row.ConversationId == conversationId)
            .OrderByDescending(row => row.TurnOrder).Take(HistoryRows).ToListAsync(cancellationToken);
        rows.Reverse();
        var image = rows.TakeLast(ImageHistoryRows).LastOrDefault(row => row.ImageObjectKey is not null);
        var messages = new List<ChatHistoryMessage>();
        foreach (var row in rows)
        {
            var content = row.Content ?? string.Empty;
            if (row.Role == "assistant")
            {
                var context = JsonSerializer.Serialize(new
                {
                    assistant_text = row.Content,
                    assistant_parts = row.ResponseJson is null ? null : ModelToolPayload.Fit(row.ResponseJson, 4_000),
                    tool_results = JsonSerializer.Deserialize<JsonElement>(row.ToolResultsJson),
                    preview_outcomes = JsonSerializer.Deserialize<JsonElement>(row.PreviewOutcomesJson)
                }, JsonOptions);
                content = ModelToolPayload.Fit(context, 12_000).ToJsonString();
            }
            byte[]? bytes = null;
            if (ReferenceEquals(image, row))
            {
                bytes = await storage.ReadPrivateAsync(row.ImageObjectKey!, cancellationToken);
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != row.ImageSha256)
                    throw new InvalidOperationException("Conversation image checksum did not match.");
            }
            messages.Add(new(row.Role, content) { ServerOwned = true, ImageData = bytes, ImageMimeType = row.ImageContentType });
        }
        return Result.Success(new StoredConversationHistory(rows.Count > 0, messages));
    }

    public async Task<ConversationPage?> GetLatestAsync(Guid userId, Guid? conversationId, long? beforeOrder, int pageSize,
        CancellationToken cancellationToken = default)
    {
        conversationId ??= await db.ConversationTurns.AsNoTracking().Where(row => row.UserId == userId && row.ConversationId != null)
            .OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.Id)
            .Select(row => row.ConversationId).FirstOrDefaultAsync(cancellationToken);
        if (conversationId is null) return null;
        var rows = await db.ConversationTurns.AsNoTracking().Where(row => row.UserId == userId && row.ConversationId == conversationId
            && (!beforeOrder.HasValue || row.TurnOrder < beforeOrder))
            .OrderByDescending(row => row.TurnOrder).Take(pageSize + 1).ToListAsync(cancellationToken);
        if (rows.Count == 0) return null;
        var hasMore = rows.Count > pageSize;
        var selected = rows.Take(pageSize).Reverse().ToArray();
        return new(conversationId.Value, selected.Select(row => new ConversationDisplayTurn(row.TurnId, row.TurnOrder!.Value,
            row.Role, row.CreatedAtUtc, row.Content, row.ResponseJson, row.PreviewOutcomesJson,
            row.ImageObjectKey is null ? null : new(Path.GetFileName(row.ImageObjectKey), row.ImageContentType!, row.ImageSha256!, $"/api/chat/conversations/{conversationId}/images/{row.TurnId}"))).ToArray(),
            hasMore ? selected[0].TurnOrder : null);
    }

    public async Task<StoredConversationImage?> ReadImageAsync(Guid userId, Guid conversationId, Guid turnId, CancellationToken cancellationToken = default)
    {
        var row = await db.ConversationTurns.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId
            && row.ConversationId == conversationId && row.TurnId == turnId && row.Role == "user", cancellationToken);
        if (row?.ImageObjectKey is null) return null;
        var bytes = await storage.ReadPrivateAsync(row.ImageObjectKey, cancellationToken);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != row.ImageSha256)
            throw new InvalidOperationException("Conversation image checksum did not match.");
        return new(bytes, row.ImageContentType!);
    }

    public Task<bool> DeleteAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default) =>
        work.ExecuteInTransactionAsync(async token =>
        {
            await work.AcquireUserRowLockAsync(userId, token);
            var query = db.ConversationTurns.Where(row => row.UserId == userId && row.ConversationId == conversationId);
            if (!await query.AnyAsync(token)) return false;
            await storage.DeleteConversationObjectsAsync(userId, conversationId, token);
            var turnIds = query.Select(row => row.TurnId);
            await db.ChatTurns.Where(row => row.UserId == userId && turnIds.Contains(row.Id)).ExecuteDeleteAsync(token);
            await query.ExecuteDeleteAsync(token);
            await db.ConversationOffers.Where(row => row.UserId == userId && row.ConversationId == conversationId).ExecuteDeleteAsync(token);
            await db.PendingAgentOperations.Where(row => row.UserId == userId && row.ConversationId == conversationId).ExecuteDeleteAsync(token);
            return true;
        }, cancellationToken);
}
