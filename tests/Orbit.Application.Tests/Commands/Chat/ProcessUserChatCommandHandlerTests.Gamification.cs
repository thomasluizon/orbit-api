using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Tests.Chat;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(true, false, false, "gamification")]
    [InlineData(false, true, false, "gamification")]
    [InlineData(false, false, true, "gamification")]
    [InlineData(true, true, false, "gamification")]
    [InlineData(false, true, true, "gamification")]
    [InlineData(true, false, true, "streak")]
    [InlineData(true, true, true, "streak")]
    public async Task GamificationRead_CarryingClientReceivesOnlySelectedSources(
        bool profile, bool achievements, bool streak, string kind)
    {
        SetupUserAndPayGate();
        GamificationReadTestData.Configure(_mediator, Today);
        var tool = new GetGamificationOverviewTool(_mediator, _habitRepo, _userDateService);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "gamification_read", JsonSerializer.Serialize(new
        { include_profile = profile, include_achievements = achievements, include_streak = streak })));
        ContinueWithReadDirective("[[orbit:read:gamification_read]]");

        var result = await handler.Handle(new(UserId, "Show my progress", ClientContext: new(
            SupportsParts: true, PartKinds: [kind], SupportsStreakCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        var part = result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Subject;
        part.Kind.Should().Be(kind);
        using var final = JsonDocument.Parse(ChatStreamEvent.Final(result.Value).ToJson());
        var wire = final.RootElement.GetProperty("response").GetProperty("parts").EnumerateArray().Single();
        wire.GetProperty("kind").GetString().Should().Be(kind);
        var block = wire.GetProperty("block");
        var state = block.GetProperty("state");
        state.GetProperty("profile").ValueKind.Should().Be(profile ? JsonValueKind.Object : JsonValueKind.Null);
        state.GetProperty("achievements").ValueKind.Should().Be(achievements ? JsonValueKind.Object : JsonValueKind.Null);
        state.GetProperty("streak").ValueKind.Should().Be(streak ? JsonValueKind.Object : JsonValueKind.Null);
        block.GetProperty("read").GetProperty("scope").GetProperty("as_of").GetString().Should().Be(Today.ToString("yyyy-MM-dd"));
        if (kind == "gamification")
            block.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("state", "surfaceId", "read");
        if (profile)
            state.GetProperty("profile").GetProperty("totalXp").GetInt32().Should().Be(120);
        if (achievements)
            state.GetProperty("achievements").GetProperty("achievements")[1].GetProperty("progressCurrent").GetInt32().Should().Be(2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GamificationRead_UnadvertisedKindKeepsLegacyResponse(bool parts, bool advertisesKind)
    {
        SetupUserAndPayGate();
        GamificationReadTestData.Configure(_mediator, Today);
        var tool = new GetGamificationOverviewTool(_mediator, _habitRepo, _userDateService);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "profile", """{"include_profile":true,"include_achievements":false,"include_streak":false}"""));
        ContinueWithReadDirective("Your progress.\n[[orbit:read:profile]]");

        var result = await handler.Handle(new(UserId, "Show my level", ClientContext: new(
            SupportsParts: parts, PartKinds: advertisesKind ? ["gamification"] : ["streak"], SupportsStreakCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Streak.Should().BeNull();
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.AiMessage.Should().Be("Your progress.");
        using var final = JsonDocument.Parse(ChatStreamEvent.Final(result.Value).ToJson());
        final.RootElement.GetProperty("response").TryGetProperty("gamification", out _).Should().BeFalse();
        final.RootElement.GetProperty("response").GetProperty("operations").EnumerateArray().Single()
            .TryGetProperty("readBlock", out _).Should().BeFalse("unadvertised kinds must not leak through operation metadata");
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Single().ReadDirective == null),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GamificationRead_LegacyCombinedKeepsStreak(bool parts)
    {
        SetupUserAndPayGate();
        GamificationReadTestData.Configure(_mediator, Today);
        var tool = new GetGamificationOverviewTool(_mediator, _habitRepo, _userDateService);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "combined", "{}"));
        ContinueWithReadDirective("[[orbit:streak]]");

        var result = await handler.Handle(new(UserId, "Show my streak", ClientContext: new(
            SupportsParts: parts, PartKinds: ["streak"], SupportsStreakCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        var card = parts ? (StreakCard)result.Value.Parts!.OfType<ChatBlockPart>().Single().Block : result.Value.Streak!;
        card.CurrentStreak.Should().Be(5);
        card.Level.Should().Be(3);
        card.TotalXp.Should().Be(120);
        card.State!.Achievements!.Achievements.Should().HaveCount(2);
    }
}
