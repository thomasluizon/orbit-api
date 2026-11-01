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
}
