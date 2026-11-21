using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

/// <summary>
/// The view of preview items that a preview fingerprint reads. It leaves out <c>Provenance</c> and <c>Removal</c>,
/// which only a client with offers gets, so one change has one fingerprint for each client context. Each value, field,
/// target, <c>RemovesData</c>, <c>Placement</c>, <c>FieldOptions</c> and validation error stays in the fingerprint.
/// </summary>
internal static class PreviewHashView
{
    public static string Compute(string operationId, IEnumerable<PendingOperationItem> items) =>
        AgentOperationFingerprint.Compute(operationId, Serialize(items));

    public static string Serialize(IEnumerable<PendingOperationItem> items) =>
        JsonSerializer.Serialize<IReadOnlyList<PendingOperationItem>>(items.Select(item => item with
        {
            Fields = item.Fields.Select(field => field with { Provenance = null, Removal = null }).ToArray()
        }).ToArray());
}
