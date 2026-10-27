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
    [InlineData("freezes", "Streak freezes are part of Orbit Pro. Some free accounts have them too.")]
    [InlineData("gamification", "XP, levels, and achievements are part of Orbit Pro. Some free accounts have them too.")]
    [InlineData("paygate", "**AI messages** are capped at **5** per day. Pro includes **250** per local calendar week")]
    public async Task DescribeFeature_ChatAndMcpServeTheSameEmbeddedBody(string key, string expectedClaim)
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
        chatBody.Should().Contain(expectedClaim);
        mcp.Should().Be($"# {explanation.DisplayName}\n\n{chatBody}");
        mcp.Should().Contain(expectedClaim);
    }
}
