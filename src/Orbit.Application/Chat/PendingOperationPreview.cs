using System.Text.Json;
using Orbit.Domain.Common;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class PendingOperationPreview
{
    public static async Task<PendingOperationChangePreview?> PreviewSafelyAsync(
        this IPendingOperationChangePreviewer previewer, Guid userId, string operationId,
        JsonElement arguments, ILogger logger, CancellationToken cancellationToken, AgentClientContext? clientContext = null)
    {
        var result = await previewer.TryPreviewAsync(userId, operationId, arguments, logger, cancellationToken, clientContext);
        return result.IsSuccess ? result.Value : null;
    }

    public static async Task<Result<PendingOperationChangePreview?>> TryPreviewAsync(
        this IPendingOperationChangePreviewer previewer, Guid userId, string operationId,
        JsonElement arguments, ILogger logger, CancellationToken cancellationToken, AgentClientContext? clientContext = null)
    {
        try
        {
            return await previewer.PreviewAsync(userId, operationId, arguments, cancellationToken, clientContext);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Pending operation change preview failed");
            return Result.Failure<PendingOperationChangePreview?>("preview_unavailable");
        }
    }
}
