using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Connections;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services.Notion;

internal sealed class NotionConnectionService(OrbitDbContext db, INotionApi api,
    IOptions<NotionSettings> options, IEncryptionService encryption, IUnitOfWork unitOfWork,
    TimeProvider clock, IProductAnalytics analytics, ILogger<NotionConnectionService> logger) : INotionConnectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<NotionAuthorizationResponse>> AuthorizeAsync(Guid userId, string redirectUri, CancellationToken ct)
    {
        if (!options.Value.IsConfigured || !encryption.IsConfigured)
            return Result.Failure<NotionAuthorizationResponse>("Notion connection is not configured.");
        if (!options.Value.Allows(redirectUri))
            return Result.Failure<NotionAuthorizationResponse>("Notion redirect URI is not allowed.");
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        await db.NotionOAuthStates.Where(s => s.ExpiresAtUtc <= nowUtc).ExecuteDeleteAsync(ct);
        db.NotionOAuthStates.Add(NotionOAuthState.Create(userId, Hash(state), redirectUri, nowUtc));
        await db.SaveChangesAsync(ct);
        var url = "https://api.notion.com/v1/oauth/authorize?owner=user&response_type=code"
            + $"&client_id={Uri.EscapeDataString(options.Value.ClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={state}";
        return Result.Success(new NotionAuthorizationResponse(url));
    }

    public async Task<Result> CompleteAsync(Guid userId, string code, string state, string redirectUri, CancellationToken ct)
    {
        if (!options.Value.IsConfigured || !encryption.IsConfigured) return Result.Failure("Notion connection is not configured.");
        if (!options.Value.Allows(redirectUri)) return Result.Failure("Notion redirect URI is not allowed.");
        var hash = Hash(state);
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var consumed = await db.NotionOAuthStates.Where(s => s.UserId == userId && s.StateHash == hash
            && s.RedirectUri == redirectUri && s.ExpiresAtUtc > nowUtc && !s.IsConsumed)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.IsConsumed, true), ct);
        if (consumed != 1) return Result.Failure("Notion authorization state is invalid or expired.");

        NotionCredentials credentials;
        try { credentials = await api.ExchangeAsync(code, redirectUri, ct); }
        catch (Exception ex) when (ex is NotionApiException or HttpRequestException or JsonException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Notion token exchange failed for {UserId}", userId);
            return Result.Failure("Notion authorization could not be completed.");
        }

        var connected = await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            await unitOfWork.AcquireUserRowLockAsync(userId, transactionToken);
            if (!await db.NotionOAuthStates.AnyAsync(s => s.UserId == userId && s.StateHash == hash && s.IsConsumed, transactionToken))
                return false;
            await DeleteLocalAsync(userId, transactionToken);
            db.NotionConnections.Add(NotionConnection.Create(userId, credentials.AccessToken, credentials.RefreshToken,
                credentials.WorkspaceId, credentials.WorkspaceName, credentials.BotId));
            await db.SaveChangesAsync(transactionToken);
            return true;
        }, ct);
        if (!connected)
        {
            await RevokeBestEffortAsync(userId, credentials.AccessToken, ct);
            return Result.Failure("Notion authorization is no longer pending.");
        }
        analytics.CaptureUserEvent(userId, "notion_connected", "pro");
        return Result.Success();
    }

    public async Task<NotionConnectionResponse> GetAsync(Guid userId, CancellationToken ct)
    {
        var connection = await db.NotionConnections.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId, ct);
        return connection is null
            ? new(false, null, nameof(NotionConnectionStatus.Idle), null, [])
            : new(connection.Status != NotionConnectionStatus.ReconnectRequired, connection.WorkspaceName,
                connection.Status.ToString(), connection.LastPollAtUtc,
                JsonSerializer.Deserialize<NotionDatabase[]>(connection.SharedDatabasesJson, JsonOptions) ?? []);
    }

    public async Task<Result> DisconnectAsync(Guid userId, CancellationToken ct)
    {
        var connection = await db.NotionConnections.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (connection is not null)
            await RevokeBestEffortAsync(userId, connection.AccessToken, ct);
        await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            await unitOfWork.AcquireUserRowLockAsync(userId, transactionToken);
            await DeleteLocalAsync(userId, transactionToken);
        }, ct);
        analytics.CaptureUserEvent(userId, "notion_disconnected", "pro");
        return Result.Success();
    }

    private async Task DeleteLocalAsync(Guid userId, CancellationToken ct)
    {
        await db.NotionTasks.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.NotionConnections.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.NotionOAuthStates.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
    }

    private async Task RevokeBestEffortAsync(Guid userId, string accessToken, CancellationToken ct)
    {
        try { await api.RevokeAsync(accessToken, ct); }
        catch (Exception ex) when (ex is NotionApiException or HttpRequestException or JsonException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Notion revocation failed for {UserId}; removing local connection data", userId);
        }
    }

    private static string Hash(string state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
}
