#pragma warning disable OPENAI001

using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiIntentServiceStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationAttachment_UsesSdkImageInputOnInitialAndFollowingTurns(bool following)
    {
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10];
        var handler = new SequenceSseHandler(ResponsesTestData.Text("Ready."));
        var (service, _) = BuildService(handler);
        var request = new AiToolRequest(following ? "ta criando na data certa?" : "cria essa tarefa unica", "system", [],
            ImageData: following ? null : bytes, ImageMimeType: following ? null : "image/png")
        {
            ConversationAttachments = following ? [new("cria essa tarefa unica", bytes, "image/png")] : []
        };
        var result = await service.SendWithToolsAsync(request);
        result.IsSuccess.Should().BeTrue(result.Error);
        using var wire = JsonDocument.Parse(handler.RequestBodies.Single());
        var messages = wire.RootElement.GetProperty("input").EnumerateArray().ToArray();
        var images = messages.SelectMany(message => message.GetProperty("content").EnumerateArray())
            .Where(part => part.GetProperty("type").GetString() == "input_image").ToArray();
        images.Should().ContainSingle().Which.GetProperty("image_url").GetString()
            .Should().Be("data:image/png;base64," + Convert.ToBase64String(bytes));
        messages.Last().GetProperty("content")[0].GetProperty("text").GetString().Should().Be(request.UserMessage);
        if (following)
            messages.SelectMany(message => message.GetProperty("content").EnumerateArray())
                .Where(part => part.GetProperty("type").GetString() == "input_text")
                .Should().Contain(part => part.GetProperty("text").GetString()!.Contains("cria essa tarefa unica", StringComparison.Ordinal));
    }
}
