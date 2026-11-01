#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Text.Json;
using FluentAssertions;
using OpenAI.Responses;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiIntentServiceStreamingTests
{
    [Theory]
    [InlineData("query_habits")]
    [InlineData("query_goals")]
    [InlineData("get_calendar_overview")]
    [InlineData("get_gamification_overview")]
    public async Task OversizedToolPayload_KeepsRowsAndReportsOmissions(string tool)
    {
        var handler = new SequenceSseHandler(ResponsesTestData.Text("Read."));
        var (service, _) = BuildService(handler);
        var rows = Enumerable.Range(0, 90).Select(index => new
        {
            id = index.ToString(),
            title = $"Matching item {index}",
            description = new string('x', 300)
        }).ToArray();
        var context = new AiConversationContext
        {
            Messages = new List<ResponseItem> { ResponseItem.CreateFunctionCallItem("read", tool, BinaryData.FromString("{}")) },
            Options = new CreateResponseOptions { Model = "gpt-6-luna" }
        };

        var result = await service.ContinueWithToolResultsAsync(context,
            [new(tool, "read", true, null, null, null, new { items = rows, totalCount = 130 }, "[[orbit:read:read]]")]);

        result.IsSuccess.Should().BeTrue(result.Error);
        using var request = JsonDocument.Parse(handler.RequestBodies.Single());
        var output = request.RootElement.GetProperty("input").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "function_call_output");
        using var body = JsonDocument.Parse(output.GetProperty("output").GetString()!);
        body.RootElement.GetProperty("readDirective").GetString().Should().Be("[[orbit:read:read]]");
        var payload = body.RootElement.GetProperty("payload");
        payload.GetRawText().Length.Should().BeLessThanOrEqualTo(AppConstants.MaxAiToolPayloadJsonLength);
        payload.TryGetProperty("dropped", out _).Should().BeFalse();
        payload.GetProperty("truncated").GetBoolean().Should().BeTrue();
        var items = payload.GetProperty("data").GetProperty("items");
        items.GetArrayLength().Should().BeInRange(1, 89);
        items[0].GetProperty("title").GetString().Should().Be(rows[0].title);
        payload.GetProperty("data").GetProperty("totalCount").GetInt32().Should().Be(130);
        var collection = payload.GetProperty("collections").EnumerateArray().Single();
        collection.GetProperty("total").GetInt32().Should().Be(90);
        collection.GetProperty("returned").GetInt32().Should().Be(items.GetArrayLength());
        collection.GetProperty("has_more").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedToolPayload_RetainsOneLargeItemAndMarksShortenedText(bool array)
    {
        var handler = new SequenceSseHandler(ResponsesTestData.Text("Read."));
        var (service, _) = BuildService(handler);
        var item = new { id = "target", title = "Reservation", description = string.Concat(Enumerable.Repeat("漢😀", 9000)) };
        var context = new AiConversationContext
        {
            Messages = new List<ResponseItem> { ResponseItem.CreateFunctionCallItem("read", "read", BinaryData.FromString("{}")) },
            Options = new CreateResponseOptions { Model = "gpt-6-luna" }
        };
        var result = await service.ContinueWithToolResultsAsync(context,
            [new("read", "read", true, null, null, null, array ? new[] { item } : (object)item)]);
        result.IsSuccess.Should().BeTrue(result.Error);
        using var request = JsonDocument.Parse(handler.RequestBodies.Single());
        var output = request.RootElement.GetProperty("input").EnumerateArray().Last();
        using var body = JsonDocument.Parse(output.GetProperty("output").GetString()!);
        var payload = body.RootElement.GetProperty("payload");
        payload.GetRawText().Length.Should().BeLessThanOrEqualTo(AppConstants.MaxAiToolPayloadJsonLength);
        var data = array ? payload.GetProperty("data")[0] : payload.GetProperty("data");
        data.GetProperty("id").GetString().Should().Be("target");
        data.GetProperty("title").GetString().Should().Be("Reservation");
        payload.GetProperty("shortened_fields").GetArrayLength().Should().BeGreaterThan(0);
    }
}
