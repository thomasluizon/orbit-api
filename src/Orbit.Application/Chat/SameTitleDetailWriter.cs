using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

/// <summary>
/// Writes the same-title detail on the preview items of a response: the parent title and the place of each record
/// that shares its title with a second habit of the account. It goes into <c>entityDetail</c> for a client with
/// <c>supportsPendingOperationEntityDetail</c>, and into <c>entityName</c> and the answer text for a different one.
/// It runs only on an open preview and after each store write, so no stored form or fingerprint holds the detail.
/// </summary>
public sealed class SameTitleDetailWriter(IGenericRepository<Habit> habits, IGenericRepository<User> users)
{
    private const string OpenOutcome = "pending";

    /// <summary>The chat response of a turn. The rename lines go into the answer text.</summary>
    public Task<ChatResponse> WriteAsync(Guid userId, ChatResponse response, AgentClientContext? client,
        string? language, CancellationToken cancellationToken) =>
        WriteAsync(response, client, language, () => LoadAsync(userId, cancellationToken));

    internal static async Task<ChatResponse> WriteAsync(ChatResponse response, AgentClientContext? client,
        string? language, Func<Task<IReadOnlyList<Habit>>> loadHabits)
    {
        if (response.PendingOperations is not { } previews || !previews.Any(HasRecord)) return response;
        var view = new ClientView(new SameTitlePlaces(await loadHabits()), client, language);
        var lines = new List<string>();
        response = response with { PendingOperations = [.. previews.Select(preview => Write(preview, view, lines))] };
        if (lines.Count == 0) return response;
        var text = AnswerLines(lines);
        return response.Parts is { Count: > 0 } parts
            ? response with { Parts = [.. parts, new ChatTextPart(text)] }
            : response with { AiMessage = string.IsNullOrWhiteSpace(response.AiMessage) ? text : response.AiMessage + "\n\n" + text };
    }

    /// <summary>The response of an execute, a resolve or a habit suggestion preview. It has no answer text.</summary>
    public async Task<AgentExecuteOperationResponse> WriteAsync(Guid userId, AgentExecuteOperationResponse response,
        AgentClientContext? client, CancellationToken cancellationToken) =>
        response.PendingOperation is { } preview
            ? response with { PendingOperation = await WriteAsync(userId, preview, client, cancellationToken) }
            : response;

    /// <summary>A pending operation that a route returns. It has no answer text.</summary>
    public async Task<PendingAgentOperation> WriteAsync(Guid userId, PendingAgentOperation preview,
        AgentClientContext? client, CancellationToken cancellationToken) =>
        HasRecord(preview)
            ? Write(preview, new(new SameTitlePlaces(await LoadAsync(userId, cancellationToken)), client, null), [])
            : preview;

    /// <summary>The result of a refresh or a revise. It has no answer text.</summary>
    public async Task<PendingOperationRevisionResult> WriteAsync(Guid userId, PendingOperationRevisionResult result,
        AgentClientContext? client, CancellationToken cancellationToken)
    {
        if (result.Preview is not { Items: { } items } preview || !items.Any(item => item.EntityId.HasValue)) return result;
        var view = new ClientView(new SameTitlePlaces(await LoadAsync(userId, cancellationToken)), client, null);
        return result with { Preview = preview with { Items = Write(items, view, []) } };
    }

    /// <summary>
    /// A conversation page. Only a preview whose stored outcome is <c>pending</c> is open. The page returns the stored
    /// response JSON as text, so the detail goes into that text and no stored row changes. A request with no client
    /// locale gets the text of the profile language, as the turn of the preview did.
    /// </summary>
    public async Task<ConversationPage> WriteAsync(Guid userId, ConversationPage page, AgentClientContext? client,
        CancellationToken cancellationToken)
    {
        var open = page.Turns.Select(OpenPreviews).ToArray();
        if (open.All(previews => previews.Count == 0)) return page;
        var language = string.IsNullOrWhiteSpace(client?.Locale)
            ? (await users.GetByIdAsync(userId, cancellationToken))?.Language : null;
        var view = new ClientView(new SameTitlePlaces(await LoadAsync(userId, cancellationToken)), client, language);
        return page with
        {
            Turns = [.. page.Turns.Select((turn, index) => open[index].Count == 0 ? turn
                : turn with { ResponseJson = Write(turn.ResponseJson!, open[index], view) })]
        };
    }

    private Task<IReadOnlyList<Habit>> LoadAsync(Guid userId, CancellationToken cancellationToken) =>
        habits.FindAsync(habit => habit.UserId == userId, cancellationToken);

    private sealed record ClientView(SameTitlePlaces Places, AgentClientContext? Client, string? Language)
    {
        public bool WithField => Client?.SupportsPendingOperationEntityDetail == true;
        public bool Portuguese => LocaleHelper.IsPortuguese(ChatLocale.Resolve(Client, Language));
    }

    /// <summary>The three values of an item that the detail reads: the record ID, the name and the new title.</summary>
    private readonly record struct ItemView(Guid? EntityId, string EntityName, string? NewTitle);

    private readonly record struct ItemDetail(string EntityName, string? EntityDetail, string? RenameLine);

    /// <summary>
    /// The one place that decides the detail of an item. Null when the item names no habit of the account, or when no
    /// second habit of the account shares the title of the record.
    /// </summary>
    private static ItemDetail? DetailOf(ItemView item, ClientView view)
    {
        if (item.EntityId is not { } id || view.Places.Find(id) is not { } place) return null;
        if (view.WithField) return new(item.EntityName, place.Detail(view.Portuguese), null);
        return new(place.Name(item.EntityName, view.Portuguese), null,
            string.IsNullOrWhiteSpace(item.NewTitle) ? null : place.Rename(item.EntityName, item.NewTitle, view.Portuguese));
    }

    private static bool HasRecord(PendingAgentOperation preview) => preview.Items?.Any(item => item.EntityId.HasValue) == true;

    private static PendingAgentOperation Write(PendingAgentOperation preview, ClientView view, List<string> lines) =>
        preview.Items is { } items ? preview with { Items = Write(items, view, lines) } : preview;

    private static PendingOperationItem[] Write(IReadOnlyList<PendingOperationItem> items, ClientView view, List<string> lines) =>
        [.. items.Select(item =>
        {
            var title = item.Fields.FirstOrDefault(field => field.Field == "title")?.NewValue;
            if (DetailOf(new(item.EntityId, item.EntityName, title), view) is not { } detail) return item;
            if (detail.RenameLine is { } line) lines.Add(line);
            return item with { EntityName = detail.EntityName, EntityDetail = detail.EntityDetail };
        })];

    private static string AnswerLines(IEnumerable<string> lines) =>
        string.Join("\n", lines.Select(line => "- " + line.ReplaceLineEndings(" ")));

    private static IReadOnlySet<string> OpenPreviews(ConversationDisplayTurn turn)
    {
        if (turn.Role != "assistant" || turn.ResponseJson is null) return new HashSet<string>();
        var outcomes = JsonSerializer.Deserialize<List<ConversationPreviewOutcome>>(turn.PreviewOutcomesJson) ?? [];
        return outcomes.Where(outcome => outcome.Outcome == OpenOutcome)
            .Select(outcome => outcome.PendingOperationId.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Write(string responseJson, IReadOnlySet<string> open, ClientView view)
    {
        if (JsonNode.Parse(responseJson) is not JsonObject response || response["pendingOperations"] is not JsonArray previews)
            return responseJson;
        var lines = new List<string>();
        var changed = false;
        foreach (var item in previews.OfType<JsonObject>()
            .Where(preview => preview["id"] is JsonValue id && open.Contains(id.ToString()))
            .SelectMany(preview => (preview["items"] as JsonArray)?.OfType<JsonObject>() ?? []))
        {
            if (DetailOf(View(item), view) is not { } detail) continue;
            changed = true;
            item["entityName"] = detail.EntityName;
            if (detail.EntityDetail is { } text) item["entityDetail"] = text;
            if (detail.RenameLine is { } line) lines.Add(line);
        }
        if (!changed) return responseJson;
        if (lines.Count > 0 && response["parts"] is JsonArray { Count: > 0 } parts)
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = AnswerLines(lines) });
        else if (lines.Count > 0)
            response["aiMessage"] = response["aiMessage"] is JsonValue message && !string.IsNullOrWhiteSpace(message.ToString())
                ? message + "\n\n" + AnswerLines(lines) : AnswerLines(lines);
        return response.ToJsonString(ChatStreamEvent.SerializerOptions);
    }

    private static ItemView View(JsonObject item) => new(
        item["entityId"] is JsonValue id && Guid.TryParse(id.ToString(), out var parsed) ? parsed : null,
        item["entityName"]?.ToString() ?? string.Empty,
        (item["fields"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(field => field["field"]?.ToString() == "title")?["newValue"]?.ToString());
}
