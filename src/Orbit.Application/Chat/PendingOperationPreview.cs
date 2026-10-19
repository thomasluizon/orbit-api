using System.Text.Json;
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
        try
        {
            return await previewer.PreviewAsync(userId, operationId, arguments, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Pending operation change preview failed for {OperationId}", operationId);
            return null;
        }
    }
}
