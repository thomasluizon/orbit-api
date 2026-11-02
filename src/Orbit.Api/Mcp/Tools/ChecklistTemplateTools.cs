using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.ChecklistTemplates.Queries;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class ChecklistTemplateTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "get_checklist_templates"), Description("Get the user's reusable checklist templates.")]
    public async Task<CallToolResult> GetChecklistTemplates(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var result = await mediator.Send(new GetChecklistTemplatesQuery(userId), cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var templates = result.Value;
            if (templates.Count == 0)
                return McpToolHelpers.Text("No checklist templates.");
            var lines = templates.Select(t => $"- {t.Name} (id: {t.Id}) | {t.Items.Count} item(s): {string.Join(", ", t.Items)}");
            return McpToolHelpers.Text($"Checklist templates ({templates.Count}):\n{string.Join("\n", lines)}");
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "create_checklist_template"), Description("Create a reusable checklist template.")]
    public async Task<CallToolResult> CreateChecklistTemplate(
        ClaimsPrincipal user,
        [Description("Template name")] string name,
        [Description("Comma-separated checklist item texts")] string items,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var itemList = items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await executorBridge.ExecuteAsync(user, "create_checklist_template", new { name, items = itemList }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Created checklist template '{name}' (id: {result.TargetId})") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "update_checklist_template"), Description("Update a checklist template's name, items, or both. Omitted fields stay unchanged.")]
    public async Task<CallToolResult> UpdateChecklistTemplate(
        ClaimsPrincipal user,
        [Description("The template ID (GUID)")] string templateId,
        [Description("Replacement template name")] string? name = null,
        [Description("Replacement checklist item texts")] string[]? items = null,
        [Description("Confirmation token if requested by the executor")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(templateId, "templateId");
            var result = await executorBridge.ExecuteAsync(user, "update_checklist_template", new { template_id = templateId, name, items }, confirmationToken, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Updated checklist template {templateId}.") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "delete_checklist_template"), Description("Delete a checklist template by ID.")]
    public async Task<CallToolResult> DeleteChecklistTemplate(
        ClaimsPrincipal user,
        [Description("The template ID (GUID)")] string templateId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(templateId, "templateId");
            var result = await executorBridge.ExecuteAsync(user, "delete_checklist_template", new { template_id = templateId }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Deleted checklist template {templateId}.") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }
}
