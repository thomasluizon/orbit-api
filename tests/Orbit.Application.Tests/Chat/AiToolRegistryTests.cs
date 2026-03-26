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
