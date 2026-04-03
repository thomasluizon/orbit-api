using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.Tools;

namespace Orbit.Application.Tests.Chat;

public class AiToolRegistryTests
{
    [Fact]
    public void GetAll_ReturnsAllRegisteredTools()
    {
        var tools = new IAiTool[] { new FakeTool("tool_a"), new FakeTool("tool_b") };
        var registry = new AiToolRegistry(tools);

        registry.GetAll().Should().HaveCount(2);
    }

    [Fact]
    public void GetTool_FindsByName_CaseInsensitive()
    {
        var tools = new IAiTool[] { new FakeTool("log_habit") };
        var registry = new AiToolRegistry(tools);

        registry.GetTool("LOG_HABIT").Should().NotBeNull();
        registry.GetTool("log_habit").Should().NotBeNull();
    }

    [Fact]
    public void GetTool_ReturnsNull_ForUnknownTool()
    {
        var tools = new IAiTool[] { new FakeTool("log_habit") };
        var registry = new AiToolRegistry(tools);

        registry.GetTool("nonexistent").Should().BeNull();
    }

    [Fact]
    public void AllTools_HaveUniqueNames()
    {
        var tools = new IAiTool[]
        {
            new FakeTool("tool_a"),
            new FakeTool("tool_b"),
            new FakeTool("tool_c"),
        };
        var registry = new AiToolRegistry(tools);

        var names = registry.GetAll().Select(t => t.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ReadOnlyTool_IsReadOnly_ReturnsTrue()
    {
        var tool = new FakeTool("read_tool", isReadOnly: true);
        tool.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void WriteTool_IsReadOnly_ReturnsFalse()
    {
        var tool = new FakeTool("write_tool", isReadOnly: false);
        tool.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void GetAll_EmptyRegistry_ReturnsEmptyList()
    {
        var registry = new AiToolRegistry(Array.Empty<IAiTool>());

        registry.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void GetTool_EmptyString_ReturnsNull()
    {
        var tools = new IAiTool[] { new FakeTool("log_habit") };
        var registry = new AiToolRegistry(tools);

        registry.GetTool("").Should().BeNull();
    }

    [Fact]
    public void GetTool_MixedCaseVariations_FindsTool()
    {
        var tools = new IAiTool[] { new FakeTool("Create_Habit") };
        var registry = new AiToolRegistry(tools);

        registry.GetTool("create_habit").Should().NotBeNull();
        registry.GetTool("CREATE_HABIT").Should().NotBeNull();
        registry.GetTool("Create_Habit").Should().NotBeNull();
    }

    [Fact]
    public void GetAll_PreservesToolOrder()
    {
        var tools = new IAiTool[]
        {
            new FakeTool("alpha"),
            new FakeTool("beta"),
            new FakeTool("gamma"),
        };
        var registry = new AiToolRegistry(tools);

        var all = registry.GetAll();
        all.Should().HaveCount(3);
    }

    [Fact]
    public void GetTool_ReturnsCorrectTool_WhenMultipleExist()
    {
        var tools = new IAiTool[]
        {
            new FakeTool("log_habit"),
            new FakeTool("create_habit"),
            new FakeTool("delete_habit"),
        };
        var registry = new AiToolRegistry(tools);

        var tool = registry.GetTool("create_habit");
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("create_habit");
    }

    [Fact]
    public void FakeTool_GetParameterSchema_ReturnsObject()
    {
        var tool = new FakeTool("test");
        var schema = tool.GetParameterSchema();
        schema.Should().NotBeNull();
    }

    [Fact]
    public async Task FakeTool_ExecuteAsync_ReturnsSuccess()
    {
        var tool = new FakeTool("test");
        var args = JsonSerializer.Deserialize<JsonElement>("{}");

        var result = await tool.ExecuteAsync(args, Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public void ToolResult_DefaultValues()
    {
        var result = new ToolResult(true);

        result.Success.Should().BeTrue();
        result.EntityId.Should().BeNull();
        result.EntityName.Should().BeNull();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void ToolResult_WithAllFields()
    {
        var result = new ToolResult(false, "123", "MyEntity", "Something went wrong");

        result.Success.Should().BeFalse();
        result.EntityId.Should().Be("123");
        result.EntityName.Should().Be("MyEntity");
        result.Error.Should().Be("Something went wrong");
    }

    private class FakeTool(string name, bool isReadOnly = false) : IAiTool
    {
        public string Name => name;
        public string Description => $"Fake tool: {name}";
        public bool IsReadOnly => isReadOnly;
        public object GetParameterSchema() => new { type = "OBJECT" };
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
            => Task.FromResult(new ToolResult(true));
    }
}
