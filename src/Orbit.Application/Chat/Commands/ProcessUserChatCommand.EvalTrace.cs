using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    internal sealed class ChatEvalTraceCollector(string traceId, AiToolRegistry registry)
    {
        private readonly List<ChatEvalToolCall> _calls = [];

        public ChatEvalTrace Build() => new(traceId, _calls.ToArray());

        private void Add(AiToolCall call, string status, AiToolCallResult? result, bool omitData)
        {
            var sensitiveFields = new HashSet<string>(StringComparer.Ordinal);
            if (registry.GetTool(call.Name) is { } tool)
            {
                CollectSensitiveFields(JsonSerializer.SerializeToElement(tool.GetParameterSchema()), sensitiveFields);
                if (tool.GetResultSchema() is { } resultSchema)
                    CollectSensitiveFields(JsonSerializer.SerializeToElement(resultSchema), sensitiveFields);
            }
            _calls.Add(new ChatEvalToolCall(call.Id, call.Name,
                omitData ? JsonSerializer.SerializeToElement("[redacted]") : Sanitize(call.Args, sensitiveFields),
                status, omitData || result is null ? null : Sanitize(result, sensitiveFields)));
        }

        private static void CollectSensitiveFields(JsonElement schema, ISet<string> sensitiveFields)
        {
            if (schema.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("properties", out var properties))
                    foreach (var property in properties.EnumerateObject())
                        if (property.Value.TryGetProperty("sensitive", out var sensitive) && sensitive.ValueKind == JsonValueKind.True)
                            sensitiveFields.Add(NormalizeFieldName(property.Name));
                foreach (var property in schema.EnumerateObject())
                    CollectSensitiveFields(property.Value, sensitiveFields);
            }
            else if (schema.ValueKind == JsonValueKind.Array)
                foreach (var item in schema.EnumerateArray())
                    CollectSensitiveFields(item, sensitiveFields);
        }

        private static JsonElement Sanitize(object value, IReadOnlySet<string> sensitiveFields)
        {
            var node = JsonSerializer.SerializeToNode(value, ChatStreamEvent.SerializerOptions);
            Redact(node, sensitiveFields);
            return JsonSerializer.SerializeToElement(node);
        }

        private static void Redact(JsonNode? node, IReadOnlySet<string> sensitiveFields)
        {
            if (node is JsonObject obj)
            {
                foreach (var property in obj.ToArray())
                {
                    var name = NormalizeFieldName(property.Key);
                    if (sensitiveFields.Contains(name))
                        obj.Remove(property.Key);
                    else if (IsSensitive(name))
                        obj[property.Key] = "[redacted]";
                    else
                        Redact(property.Value, sensitiveFields);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var item in array)
                    Redact(item, sensitiveFields);
            }
        }

        private static string NormalizeFieldName(string name) => name.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

        private static bool IsSensitive(string name) => name.Contains("password", StringComparison.Ordinal)
            || name.Contains("secret", StringComparison.Ordinal)
            || name.Contains("token", StringComparison.Ordinal)
            || name.Contains("encrypted", StringComparison.Ordinal)
            || name.Contains("systemprompt", StringComparison.Ordinal)
            || name.Contains("conversation", StringComparison.Ordinal)
            || name is "apikey" or "rawkey" or "authorization";

        public void Record(AiToolCall call, AiToolCallResult? result, AgentOperationStatus? status, bool omitData)
        {
            var label = result is null ? "NotExecuted"
                : status?.ToString() ?? (result.Success ? "Succeeded" : "Failed");
            Add(call, label, result, omitData);
        }
    }
}
