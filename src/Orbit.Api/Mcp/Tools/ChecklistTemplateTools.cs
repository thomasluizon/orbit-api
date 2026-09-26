using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.ChecklistTemplates.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class ChecklistTemplateTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "get_checklist_templates"), Description("Get the user's reusable checklist templates.")]
    public async Task<string> GetChecklistTemplates(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var result = await mediator.Send(new GetChecklistTemplatesQuery(userId), cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var templates = result.Value;
        if (templates.Count == 0)
            return "No checklist templates.";

        var lines = templates.Select(t =>
            $"- {t.Name} (id: {t.Id}) | {t.Items.Count} item(s): {string.Join(", ", t.Items)}");

        return $"Checklist templates ({templates.Count}):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "create_checklist_template"), Description("Create a reusable checklist template.")]
    public async Task<string> CreateChecklistTemplate(
        ClaimsPrincipal user,
        [Description("Template name")] string name,
        [Description("Comma-separated checklist item texts")] string items,
        CancellationToken cancellationToken = default)
    {
        var itemList = items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = await executorBridge.ExecuteAsync(user, "create_checklist_template", new
        {
            name,
            items = itemList
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded
            ? $"Created checklist template '{name}' (id: {result.TargetId})"
            : result.Message;
    }

    [McpServerTool(Name = "delete_checklist_template"), Description("Delete a checklist template by ID.")]
    public async Task<string> DeleteChecklistTemplate(
        ClaimsPrincipal user,
        [Description("The template ID (GUID)")] string templateId,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "delete_checklist_template", new
        {
            template_id = templateId
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Deleted checklist template {templateId}." : result.Message;
    }
}
