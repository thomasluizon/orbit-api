using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public class ChatEvalTraceCollectorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Record_UsesNestedContractMarkersForArgumentsAndResultsWithoutMutatingInputs(bool execute)
    {
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns("contract_tool");
        tool.GetParameterSchema().Returns(JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"items":{"type":"array","items":{"anyOf":[
              {"type":"object","properties":{"delivery":{"type":"string","sensitive":true}}}
            ]}}}}
            """));
        tool.GetResultSchema().Returns(new
        {
            type = "object",
            properties = new { receipt = new { type = "string", sensitive = true } }
        });
        var arguments = JsonSerializer.Deserialize<JsonElement>("""
            {"items":[{"delivery":"private-delivery","label":"visible"}],"receipt":"private-receipt"}
            """);
        var payload = new { items = new[] { new { delivery = "private-delivery", receipt = "private-receipt", label = "visible" } } };
        var result = execute ? new AiToolCallResult(tool.Name, "call", true, null, null, null, payload) : null;
        var collector = new ProcessUserChatCommandHandler.ChatEvalTraceCollector("request", new AiToolRegistry([tool]));

        collector.Record(new AiToolCall(tool.Name, "call", arguments), result, null, false);

        var call = collector.Build().ToolCalls.Should().ContainSingle().Subject;
        call.Status.Should().Be(execute ? "Succeeded" : "NotExecuted");
        call.Arguments.GetProperty("items")[0].TryGetProperty("delivery", out _).Should().BeFalse();
        call.Arguments.GetProperty("items")[0].GetProperty("label").GetString().Should().Be("visible");
        call.Arguments.TryGetProperty("receipt", out _).Should().BeFalse();
        JsonSerializer.Serialize(collector.Build()).Should().NotContain("private-delivery").And.NotContain("private-receipt");
        if (execute)
        {
            var item = call.Result!.Value.GetProperty("payload").GetProperty("items")[0];
            item.TryGetProperty("delivery", out _).Should().BeFalse();
            item.TryGetProperty("receipt", out _).Should().BeFalse();
            item.GetProperty("label").GetString().Should().Be("visible");
        }
        arguments.GetProperty("items")[0].GetProperty("delivery").GetString().Should().Be("private-delivery");
        payload.items[0].delivery.Should().Be("private-delivery");
        payload.items[0].receipt.Should().Be("private-receipt");
    }
}
