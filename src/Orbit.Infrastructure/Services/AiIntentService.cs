using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class ClaudeIntentService(
    HttpClient httpClient,
    IOptions<ClaudeSettings> options,
    ILogger<ClaudeIntentService> logger) : IAiIntentService
{
    private readonly ClaudeSettings _settings = options.Value;

    private static readonly JsonSerializerOptions ActionPlanJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<Result<AiActionPlan>> InterpretAsync(
        string userMessage,
        IReadOnlyList<Habit> activeHabits,
        IReadOnlyList<TaskItem> pendingTasks,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildSystemPrompt(activeHabits, pendingTasks);

        var request = new ClaudeRequest(
            _settings.Model,
            MaxTokens: 1024,
            systemPrompt,
            [new ClaudeMessage("user", userMessage)]);

        try
        {
            var response = await httpClient.PostAsJsonAsync("v1/messages", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Claude API returned {Status}: {Body}", response.StatusCode, errorBody);
                return Result.Failure<AiActionPlan>($"Claude API error: {response.StatusCode}");
            }

            var claudeResponse = await response.Content.ReadFromJsonAsync<ClaudeResponse>(cancellationToken);
            var text = claudeResponse?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<AiActionPlan>("Claude returned an empty response.");

            logger.LogDebug("Claude raw response: {Text}", text);

            var plan = JsonSerializer.Deserialize<AiActionPlan>(text, ActionPlanJsonOptions);

            if (plan is null)
                return Result.Failure<AiActionPlan>("Failed to deserialize AI response.");

            return Result.Success(plan);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize Claude response");
            return Result.Failure<AiActionPlan>($"Failed to parse AI response: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Claude API call failed");
            return Result.Failure<AiActionPlan>($"AI service error: {ex.Message}");
        }
    }

    // --- Claude API DTOs ---

    private record ClaudeRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] ClaudeMessage[] Messages);

    private record ClaudeMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private record ClaudeResponse(
        [property: JsonPropertyName("content")] ClaudeContentBlock[]? Content);

    private record ClaudeContentBlock(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);

    // --- System Prompt Builder ---

    private static string BuildSystemPrompt(
        IReadOnlyList<Habit> activeHabits,
        IReadOnlyList<TaskItem> pendingTasks)
    {
        var sb = new StringBuilder();

        sb.AppendLine("""
            You are Orbit AI, a life management assistant. Your ONLY job is to interpret the user's
            natural language message and return a structured JSON action plan. You must NEVER respond
            with conversational text outside of the JSON structure.
            """);

        sb.AppendLine("## Rules");
        sb.AppendLine("1. ALWAYS respond with valid JSON matching the schema below.");
        sb.AppendLine("2. Map user input to EXISTING habits by their ID when there is a clear semantic match.");
        sb.AppendLine("3. If the user mentions an activity that does NOT match any existing habit, use CreateHabit.");
        sb.AppendLine("4. If the user mentions a one-time action with a future date, use CreateTask.");
        sb.AppendLine("5. If the user mentions completing or cancelling an existing task, use UpdateTask with the task ID.");
        sb.AppendLine("6. A single message may contain MULTIPLE actions. Extract ALL of them.");
        sb.AppendLine("7. Default the date to TODAY if not explicitly specified by the user.");
        sb.AppendLine("8. For quantifiable habits, extract the numeric value from the message when possible.");
        sb.AppendLine();

        sb.AppendLine("## User's Active Habits");
        if (activeHabits.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var habit in activeHabits)
            {
                var typeLabel = habit.Type == HabitType.Quantifiable
                    ? $"Quantifiable (Unit: {habit.Unit}, Target: {habit.TargetValue})"
                    : "Boolean (Done/Not Done)";

                sb.AppendLine($"- ID: {habit.Id} | \"{habit.Title}\" | {habit.Frequency} | {typeLabel}");
            }
        }

        sb.AppendLine();

        sb.AppendLine("## User's Pending Tasks");
        if (pendingTasks.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var task in pendingTasks)
            {
                var due = task.DueDate?.ToString("yyyy-MM-dd") ?? "No due date";
                sb.AppendLine($"- ID: {task.Id} | \"{task.Title}\" | Status: {task.Status} | Due: {due}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"## Today's Date: {DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}");
        sb.AppendLine();

        sb.AppendLine("## Response JSON Schema");
        sb.AppendLine("""
            {
              "actions": [
                {
                  "type": "LogHabit | CreateHabit | CreateTask | UpdateTask",
                  "habitId": "GUID (required for LogHabit)",
                  "title": "string (required for CreateHabit, CreateTask)",
                  "description": "string (optional)",
                  "value": "number (required for quantifiable LogHabit)",
                  "dueDate": "yyyy-MM-dd (optional)",
                  "taskId": "GUID (required for UpdateTask)",
                  "newStatus": "Completed | Cancelled | InProgress (required for UpdateTask)",
                  "frequency": "Daily | Weekly | Monthly | Custom (for CreateHabit, default: Daily)",
                  "habitType": "Boolean | Quantifiable (for CreateHabit, default: Boolean)",
                  "unit": "string (required when habitType is Quantifiable)"
                }
              ],
              "aiMessage": "A brief, friendly confirmation message to display to the user."
            }
            """);

        return sb.ToString();
    }
}
