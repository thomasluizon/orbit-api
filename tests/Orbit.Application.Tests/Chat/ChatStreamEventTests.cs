using FluentAssertions;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;

namespace Orbit.Application.Tests.Chat;

public class ChatStreamEventTests
{
    [Fact]
    public void Started_And_Reset_SerializeTypeOnly()
    {
        ChatStreamEvent.Started().ToJson().Should().Be("""{"type":"started"}""");
        ChatStreamEvent.Reset().ToJson().Should().Be("""{"type":"reset"}""");
    }

    [Fact]
    public void Delta_SerializesCamelCaseAndOmitsNulls()
    {
        ChatStreamEvent.Delta("Hel").ToJson().Should().Be("""{"type":"delta","text":"Hel"}""");
    }

    [Fact]
    public void Round_SerializesIteration()
    {
        ChatStreamEvent.Round(2).ToJson().Should().Be("""{"type":"round","iteration":2}""");
    }

    [Fact]
    public void Failure_SerializesStatusErrorAndCode()
    {
        ChatStreamEvent.Failure(403, "Upgrade required", "paygate").ToJson()
            .Should().Be("""{"type":"error","status":403,"error":"Upgrade required","code":"paygate"}""");
    }

    [Fact]
    public void Failure_WithoutCode_OmitsCode()
    {
        ChatStreamEvent.Failure(500, "AI service temporarily unavailable").ToJson()
            .Should().Be("""{"type":"error","status":500,"error":"AI service temporarily unavailable"}""");
    }

    [Fact]
    public void Final_SerializesChatResponseWithStringEnumsAndCamelCase()
    {
        var response = new ChatResponse(
            "done",
            [new ActionResult("CreateHabit", ActionStatus.Success, EntityName: "Read")],
            CorrelationId: "trace-1");

        var json = ChatStreamEvent.Final(response).ToJson();

        json.Should().Contain("""
            "type":"final"
            """.Trim());
        json.Should().Contain("""
            "aiMessage":"done"
            """.Trim());
        json.Should().Contain("""
            "status":"Success"
            """.Trim());
        json.Should().Contain("""
            "correlationId":"trace-1"
            """.Trim());
    }
}
