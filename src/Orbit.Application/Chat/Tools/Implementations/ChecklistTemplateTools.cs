using System.Text.Json;
using MediatR;
using Orbit.Application.Chat.Tools;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetChecklistTemplatesTool(IMediator mediator) : IAiTool
{
    public string Name => "get_checklist_templates";
    public string Description => "Read the user's reusable checklist templates.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetChecklistTemplatesQuery(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }
}

public class CreateChecklistTemplateTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "create_checklist_template";
    public string Description => "Create a reusable checklist template.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            name = new { type = JsonSchemaTypes.String },
            items = new
            {
                type = JsonSchemaTypes.Array,
                items = new { type = JsonSchemaTypes.String }
            }
        },
        required = new[] { "name", "items" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var name = JsonArgumentParser.GetOptionalString(args, "name");
        var items = JsonArgumentParser.ParseStringArray(args, "items");

        if (string.IsNullOrWhiteSpace(name) || items is null || items.Count == 0)
            return new ToolResult(false, Error: "name and at least one item are required.");

        var command = new CreateChecklistTemplateCommand(userId, name, items);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: result.Value.ToString(), EntityName: name, Payload: new { id = result.Value, name, items })
            : ToolResult.FromFailure(result);
    }
}

public class DeleteChecklistTemplateTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "delete_checklist_template";
    public string Description => "Delete a checklist template by ID.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            template_id = new { type = JsonSchemaTypes.String }
        },
        required = new[] { "template_id" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var templateId = JsonArgumentParser.GetOptionalString(args, "template_id");
        if (!Guid.TryParse(templateId, out var parsedId))
            return new ToolResult(false, Error: "template_id must be a valid GUID.");

        var command = new DeleteChecklistTemplateCommand(userId, parsedId);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: parsedId.ToString(), EntityName: "Deleted checklist template", Payload: new { id = parsedId })
            : ToolResult.FromFailure(result, parsedId.ToString());
    }
}
