using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    internal sealed class ChatEvalTraceCollector(string traceId)
    {
        private readonly List<ChatEvalToolCall> _calls = [];

        public ChatEvalTrace Build() => new(traceId, _calls.ToArray());

        private void Add(AiToolCall call, string status, AiToolCallResult? result, bool omitData)
        {
            var redactApiKey = string.Equals(call.Name, "manage_api_keys", StringComparison.OrdinalIgnoreCase);
            _calls.Add(new ChatEvalToolCall(call.Id, call.Name,
                omitData ? JsonSerializer.SerializeToElement("[redacted]") : Sanitize(call.Args, redactApiKey),
                status, omitData || result is null ? null : Sanitize(result, redactApiKey)));
        }

        private static JsonElement Sanitize(object value, bool redactApiKey)
        {
            var node = JsonSerializer.SerializeToNode(value, ChatStreamEvent.SerializerOptions);
            Redact(node, redactApiKey);
            return JsonSerializer.SerializeToElement(node);
        }

        private static void Redact(JsonNode? node, bool redactApiKey)
        {
            if (node is JsonObject obj)
            {
                foreach (var property in obj.ToArray())
                {
                    var name = property.Key.Replace("_", "", StringComparison.Ordinal)
                        .Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
                    if (IsSensitive(name) || redactApiKey && name == "key")
                        obj[property.Key] = "[redacted]";
                    else
                        Redact(property.Value, redactApiKey);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var item in array)
                    Redact(item, redactApiKey);
            }
        }

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
