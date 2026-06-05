using System.Text.Json;
using MediatR;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;

namespace Orbit.Application.Chat.Tools.Implementations;

public class ListTagsTool(IMediator mediator) : IAiTool
{
    public string Name => "list_tags";
    public string Description => "List the user's tags with their colors.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetTagsQuery(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : new ToolResult(false, Error: result.Error);
    }
}

public class CreateTagTool(IMediator mediator) : IAiTool
{
    public string Name => "create_tag";
    public string Description => "Create a new tag with a name and hex color.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            name = new { type = JsonSchemaTypes.String, description = "Tag name" },
            color = new { type = JsonSchemaTypes.String, description = "Hex color code, e.g. #FF5733" }
        },
        required = new[] { "name", "color" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var name = JsonArgumentParser.GetOptionalString(args, "name");
        var color = JsonArgumentParser.GetOptionalString(args, "color");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(color))
            return new ToolResult(false, Error: "name and color are required.");

        var result = await mediator.Send(new CreateTagCommand(userId, name, color), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: result.Value.ToString(), EntityName: name)
            : new ToolResult(false, Error: result.Error);
    }
}

public class UpdateTagTool(IMediator mediator) : IAiTool
{
    public string Name => "update_tag";
    public string Description => "Update a tag's name and color.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            tag_id = new { type = JsonSchemaTypes.String, description = "ID of the tag to update" },
            name = new { type = JsonSchemaTypes.String, description = "New tag name" },
            color = new { type = JsonSchemaTypes.String, description = "New hex color code, e.g. #FF5733" }
        },
        required = new[] { "tag_id", "name", "color" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var tagIdValue = JsonArgumentParser.GetOptionalString(args, "tag_id");
        if (!Guid.TryParse(tagIdValue, out var tagId))
            return new ToolResult(false, Error: "tag_id is required and must be a valid GUID.");

        var name = JsonArgumentParser.GetOptionalString(args, "name");
        var color = JsonArgumentParser.GetOptionalString(args, "color");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(color))
            return new ToolResult(false, Error: "name and color are required.");

        var result = await mediator.Send(new UpdateTagCommand(userId, tagId, name, color), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: tagId.ToString(), EntityName: name)
            : new ToolResult(false, Error: result.Error);
    }
}

public class DeleteTagTool(IMediator mediator) : IAiTool
{
    public string Name => "delete_tag";
    public string Description => "Delete a tag. Use only when the user clearly wants a tag removed.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            tag_id = new { type = JsonSchemaTypes.String, description = "ID of the tag to delete" }
        },
        required = new[] { "tag_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var tagIdValue = JsonArgumentParser.GetOptionalString(args, "tag_id");
        if (!Guid.TryParse(tagIdValue, out var tagId))
            return new ToolResult(false, Error: "tag_id is required and must be a valid GUID.");

        var result = await mediator.Send(new DeleteTagCommand(userId, tagId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: tagId.ToString(), EntityName: "Deleted tag")
            : new ToolResult(false, Error: result.Error);
    }
}
