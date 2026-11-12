using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Orbit.Domain.Common;

namespace Orbit.Domain.Models;

public sealed record AiPlanStep(string Id, string Text, string Status,
    string Tool, string Phase,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolCallId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PendingOperationId = null);

public sealed record AiPlan(Guid Id, string Title, IReadOnlyList<AiPlanStep> Steps)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static bool IsShortText(string? text) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= 40 && !text.Any(char.IsControl);

    public bool IsValid() => Id != Guid.Empty && IsShortText(Title) && Steps is { Count: >= 2 }
        && Steps.All(step => step is not null && !string.IsNullOrWhiteSpace(step.Id) && IsShortText(step.Text)
            && !string.IsNullOrWhiteSpace(step.Tool) && step.Tool.Length <= 100
            && !step.Text.Contains(step.Tool, StringComparison.OrdinalIgnoreCase)
            && step.Phase is "execution" or "approval"
            && step.Status is "pending" or "running" or "done" or "failed" or "skipped"
            && step.PendingOperationId != Guid.Empty)
        && Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() == Steps.Count;

    public static Result<AiPlan> Create(string title, IReadOnlyList<AiPlanStep> steps)
    {
        var plan = new AiPlan(Guid.NewGuid(), title, steps.ToArray());
        return plan.IsValid() ? Result.Success(plan) : Result.Failure<AiPlan>("Plan is invalid.");
    }

    public AiPlan Change(string stepId, string status, string? callId = null, Guid? pendingId = null)
    {
        if (status is not ("pending" or "running" or "done" or "failed" or "skipped"))
            throw new ArgumentException("Plan status is invalid.", nameof(status));
        return this with
        {
            Steps = Steps.Select(step => step.Id == stepId
            ? step with { Status = status, ToolCallId = callId ?? step.ToolCallId, PendingOperationId = pendingId ?? step.PendingOperationId }
            : step).ToArray()
        };
    }

    public static AiPlan? Read(string? responseJson)
    {
        if (responseJson is null) return null;
        using var document = JsonDocument.Parse(responseJson);
        return document.RootElement.TryGetProperty("plan", out var value) && value.ValueKind != JsonValueKind.Null
            ? value.Deserialize<AiPlan>(JsonOptions) : null;
    }

    public static string? Write(string? responseJson, AiPlan? plan)
    {
        if (responseJson is null || plan is null) return responseJson;
        if (!plan.IsValid()) throw new ArgumentException("Plan is invalid.", nameof(plan));
        var response = JsonNode.Parse(responseJson)!.AsObject();
        response["plan"] = JsonSerializer.SerializeToNode(plan, JsonOptions);
        return response.ToJsonString();
    }

    public AiPlan PreviewOutcome(Guid pendingId, string outcome) => this with
    {
        Steps = Steps.Select(step => step.PendingOperationId == pendingId && step.Phase == "approval"
            && step.Status is "pending" or "running"
            ? step with { Status = outcome switch { "rejected" or "expired" => "skipped", "approved" => "running", _ => step.Status } }
            : step).ToArray()
    };
}
