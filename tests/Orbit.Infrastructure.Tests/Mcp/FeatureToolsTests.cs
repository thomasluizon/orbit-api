using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Infrastructure.Tests.Mcp;

public class FeatureToolsTests
{
    [Theory]
    [InlineData("freezes")]
    [InlineData("gamification")]
    [InlineData("paygate")]
    public async Task DescribeFeature_ChatAndMcpServeTheSameEmbeddedBody(string key)
    {
        IFeatureExplanationService features = new FeatureExplanationService();
        var explanation = features.Get(key);
        explanation.Should().NotBeNull();
        var args = JsonSerializer.SerializeToElement(new { feature_key = key });

        var chat = await new DescribeFeatureTool(features).ExecuteAsync(args, Guid.NewGuid(), CancellationToken.None);
        var mcp = new FeatureTools(features).DescribeFeature(new ClaimsPrincipal(), key);

        chat.Success.Should().BeTrue();
        var chatBody = JsonSerializer.SerializeToElement(chat.Payload).GetProperty("markdown").GetString();
        chatBody.Should().Be(explanation!.Body);
        mcp.Should().Be($"# {explanation.DisplayName}\n\n{chatBody}");
    }
}
