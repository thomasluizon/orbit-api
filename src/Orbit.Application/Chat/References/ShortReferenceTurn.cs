using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.References;

/// <summary>
/// The inputs of short references that the chat handler gets from the host: the setting <c>AI:ShortReferences</c> and
/// the records of the listed types that the turn context does not read.
/// </summary>
public sealed record ChatReferenceDependencies(
    bool ShortReferences,
    IGenericRepository<Notification> Notifications,
    IGenericRepository<ApiKey> ApiKeys,
    IGenericRepository<HabitSkipUndo> Skips);

/// <summary>
/// The chat boundary of one turn. Each model call of the turn goes through <see cref="Model"/>: the model text that it
/// sends holds references when the setting is on, and each tool call that it returns goes through the normalizer.
/// </summary>
public sealed class ShortReferenceTurn : IDisposable
{
    private readonly ConcurrentDictionary<string, NormalizedToolCall> _failures = new(StringComparer.Ordinal);
    private readonly bool _writeReferences;

    public ShortReferenceTurn(ReferenceBook book, ToolCallReferenceNormalizer normalizer, bool writeReferences, IAiIntentService inner)
    {
        Book = book;
        _writeReferences = writeReferences;
        Model = new ReferencedAiIntentService(inner, book, normalizer, writeReferences, _failures);
    }

    public ReferenceBook Book { get; }

    public IAiIntentService Model { get; }

    /// <summary>The normalized call whose arguments name a record that the person does not have, or null.</summary>
    public NormalizedToolCall? FailureOf(string callId) => _failures.GetValueOrDefault(callId);

    /// <summary>The text as the model reads it: with references when the setting is on, and as it is when the setting is off.</summary>
    public Task<string> ModelTextAsync(string text, CancellationToken ct) => _writeReferences ? Book.WriteAsync(text, ct) : Task.FromResult(text);

    public void Dispose() => Book.Dispose();

    private sealed class ReferencedAiIntentService(IAiIntentService inner, ReferenceBook book, ToolCallReferenceNormalizer normalizer,
        bool writeReferences, ConcurrentDictionary<string, NormalizedToolCall> failures) : IAiIntentService
    {
        /// <summary>The options that the provider adapter serializes a tool payload with, so a written copy reads the same.</summary>
        private static readonly JsonSerializerOptions PayloadOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public async Task<Result<AiResponse>> SendWithToolsAsync(AiToolRequest request, Func<AiStreamEvent, Task>? streamSink = null,
            CancellationToken cancellationToken = default)
        {
            if (writeReferences)
                request = request with
                {
                    SystemPrompt = await book.WriteAsync(request.SystemPrompt, cancellationToken),
                    AccountContext = request.AccountContext is null ? null : await book.WriteAsync(request.AccountContext, cancellationToken),
                    DynamicContext = request.DynamicContext is null ? null : await book.WriteAsync(request.DynamicContext, cancellationToken),
                    History = request.History is null ? null : await WriteHistoryAsync(request.History, cancellationToken)
                };
            return await NormalizeAsync(await inner.SendWithToolsAsync(request, streamSink, cancellationToken), cancellationToken);
        }

        public async Task<Result<AiResponse>> ContinueWithToolResultsAsync(AiConversationContext conversationContext,
            IReadOnlyList<AiToolCallResult> results, Func<AiStreamEvent, Task>? streamSink = null, CancellationToken cancellationToken = default)
        {
            if (writeReferences)
            {
                if (conversationContext.ResponseValidationFeedback is { } feedback)
                    conversationContext.ResponseValidationFeedback = await book.WriteAsync(feedback, cancellationToken);
                var written = new List<AiToolCallResult>(results.Count);
                foreach (var result in results)
                    written.Add(await WriteResultAsync(result, cancellationToken));
                results = written;
            }
            return await NormalizeAsync(await inner.ContinueWithToolResultsAsync(conversationContext, results, streamSink, cancellationToken),
                cancellationToken);
        }

        private async Task<IReadOnlyList<ChatHistoryMessage>> WriteHistoryAsync(IReadOnlyList<ChatHistoryMessage> history, CancellationToken ct)
        {
            var written = new List<ChatHistoryMessage>(history.Count);
            foreach (var entry in history)
                written.Add(entry with { Content = await book.WriteAsync(entry.Content, ct) });
            return written;
        }

        /// <summary>
        /// The copy of a tool result that goes to the model. The client blocks, the stored record and the eval trace read
        /// the result of the handler, so they keep full GUIDs.
        /// </summary>
        private async Task<AiToolCallResult> WriteResultAsync(AiToolCallResult result, CancellationToken ct)
        {
            var written = result with
            {
                EntityId = result.EntityId is null ? null : await book.WriteAsync(result.EntityId, ct),
                EntityName = result.EntityName is null ? null : await book.WriteAsync(result.EntityName, ct),
                Error = result.Error is null ? null : await book.WriteAsync(result.Error, ct)
            };
            if (result.Payload is null)
                return written;
            var payload = JsonSerializer.Serialize(result.Payload, PayloadOptions);
            var writtenPayload = await book.WriteAsync(payload, ct);
            return writtenPayload == payload ? written : written with { Payload = JsonSerializer.Deserialize<JsonElement>(writtenPayload) };
        }

        private async Task<Result<AiResponse>> NormalizeAsync(Result<AiResponse> response, CancellationToken ct)
        {
            if (response.IsFailure || response.Value.ToolCalls is not { Count: > 0 } calls)
                return response;
            var normalized = new List<AiToolCall>(calls.Count);
            foreach (var call in calls)
            {
                var outcome = await normalizer.NormalizeAsync(call, ct);
                if (!outcome.IsResolved)
                    failures[call.Id] = outcome;
                normalized.Add(outcome.Call);
            }
            return Result.Success(response.Value with { ToolCalls = normalized });
        }
    }
}
