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
        JsonElement arguments, ILogger logger, CancellationToken cancellationToken)
    {
        var result = await previewer.TryPreviewAsync(userId, operationId, arguments, logger, cancellationToken);
        return result.IsSuccess ? result.Value : null;
    }

    public static async Task<Result<PendingOperationChangePreview?>> TryPreviewAsync(
        this IPendingOperationChangePreviewer previewer, Guid userId, string operationId,
        JsonElement arguments, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await previewer.PreviewAsync(userId, operationId, arguments, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Pending operation change preview failed");
            return Result.Failure<PendingOperationChangePreview?>("preview_unavailable");
        }
    }
}
