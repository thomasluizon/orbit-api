#pragma warning disable OPENAI001

using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiIntentServiceStreamingTests
{
    [Fact]
    public async Task CurrentAttachment_UsesSdkImageInput()
    {
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10];
        var handler = new SequenceSseHandler(ResponsesTestData.Text("Ready."));
        var (service, _) = BuildService(handler);
        var request = new AiToolRequest("cria essa tarefa unica", "system", [],
            ImageData: bytes, ImageMimeType: "image/png");
        var result = await service.SendWithToolsAsync(request);
        result.IsSuccess.Should().BeTrue(result.Error);
        using var wire = JsonDocument.Parse(handler.RequestBodies.Single());
        var messages = wire.RootElement.GetProperty("input").EnumerateArray().ToArray();
        var images = messages.SelectMany(message => message.GetProperty("content").EnumerateArray())
            .Where(part => part.GetProperty("type").GetString() == "input_image").ToArray();
        images.Should().ContainSingle().Which.GetProperty("image_url").GetString()
            .Should().Be("data:image/png;base64," + Convert.ToBase64String(bytes));
        messages.Last().GetProperty("content")[0].GetProperty("text").GetString().Should().Be(request.UserMessage);

    }
    [Fact]
    public async Task ThreeRounds_SendAttachmentOnlyInTheFirstRequest()
    {
        var toolRound = ResponsesTestData.Response();
        toolRound.OutputItems.Add(OpenAI.Responses.ResponseItem.CreateFunctionCallItem("call", "query_habits", BinaryData.FromString("{}")));
        var handler = new SequenceSseHandler(
            System.ClientModel.Primitives.ModelReaderWriter.Write(toolRound).ToString(),
            System.ClientModel.Primitives.ModelReaderWriter.Write(toolRound).ToString(), ResponsesTestData.Text("Done."));
        var (service, _) = BuildService(handler);
        var first = await service.SendWithToolsAsync(new AiToolRequest("Inspect", "system", [new { name = "query_habits" }],
            ImageData: [137, 80, 78, 71], ImageMimeType: "image/png"));
        first.IsSuccess.Should().BeTrue(first.Error);
        var second = await service.ContinueWithToolResultsAsync(first.Value.ConversationContext!,
            [new("query_habits", "call", true, null, null, null)]);
        second.IsSuccess.Should().BeTrue(second.Error);
        var third = await service.ContinueWithToolResultsAsync(second.Value.ConversationContext!,
            [new("query_habits", "call", true, null, null, null)]);
        third.IsSuccess.Should().BeTrue(third.Error);
        handler.RequestBodies.Should().HaveCount(3);
        handler.RequestBodies[0].Should().Contain("data:image/png;base64,");
        handler.RequestBodies.Skip(1).Should().OnlyContain(body => !body.Contains("data:image/", StringComparison.Ordinal)
            && body.Contains("Image supplied in the first round", StringComparison.Ordinal));
    }

}
