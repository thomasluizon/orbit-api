using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// "The server holds nothing" for the typed change list (thomasluizon/orbit-tickets#2160): a chat round sends no
/// <c>PreviewOnly</c>, so the rule depends on the hold of the chat surface. No write tool that a change can run saves
/// before the person's approval. The lists come from the real chat registry, so a new write tool joins these tests.
/// </summary>
public partial class AgentChatWriteHoldTests
{
    /// <summary>The write tools that a change can run: each change kind and each single form that merges into a kind.</summary>
    private static IAiTool[] ChangeListWriteTools() => [.. StrictChatToolSchemaTests.AllTools()
        .Where(tool => !tool.IsReadOnly && tool is not IMcpOnlyAiTool).OrderBy(tool => tool.Name, StringComparer.Ordinal)];

    public static TheoryData<string> ChangeListWriteToolNames() => [.. ChangeListWriteTools().Select(tool => tool.Name)];

    [Fact]
    public void ChangeListWriteTools_HoldEachChangeKindAndEachToolThatAChangeDispatches()
    {
        var names = ChangeListWriteTools().Select(tool => tool.Name).ToArray();
        var kinds = ChangeKinds.All(new AiToolRegistry(StrictChatToolSchemaTests.AllTools())).Select(tool => tool.Name).ToArray();

        names.Should().Contain(kinds);
        names.Select(ChangeKinds.KindOf).Distinct().Should().BeEquivalentTo(kinds, "each other write tool is the single form of a kind");
        var single = ChangeKinds.Dispatch(new AiToolCall("bulk_create_habits", "change",
            Parse("""{"habits":[{"title":"Walk"}]}""")));
        single.Name.Should().NotBe("bulk_create_habits");
        names.Should().Contain(single.Name);
    }

    [Theory]
    [MemberData(nameof(ChangeListWriteToolNames))]
    public async Task ExecuteAsync_EachWriteToolOfAChangeList_WaitsForApprovalAndDoesNotRun(string name)
    {
        var tool = new StubTool(name);
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, name, Parse("""{"action":"unsupported_action"}"""), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation, name);
        response.PendingOperation.Should().NotBeNull(name);
        tool.Calls.Should().Be(0, name);
    }

    /// <summary>
    /// A tool that asks first is the one exemption of the chat hold. This closed list fails for a new tool of that type,
    /// until a test proves that its question path saves nothing.
    /// </summary>
    [Fact]
    public void ChangeListWriteTools_OnlyCreateHabitRunsBeforeTheHold() =>
        ChangeListWriteTools().Where(tool => tool is IClarificationPrecheckTool).Select(tool => tool.Name)
            .Should().Equal("create_habit");

    [Theory]
    [InlineData("Walking habit")]
    [InlineData("Hábito de caminhada")]
    public async Task ExecuteAsync_CreateHabitThatAsksFirst_SavesNothing(string title)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var tool = new CreateHabitTool(Substitute.For<IUserDateService>(), mediator);
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, tool.Name, JsonSerializer.SerializeToElement(new { title }), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.Operation.Payload.Should().BeOfType<NeedsClarificationPayload>();
        response.PendingOperation.Should().BeNull();
        mediator.ReceivedCalls().Select(call => call.GetArguments()[0]).Should().AllBeOfType<CheckChatCommandQuery>(
            "the question path reads the creation capacity and sends no command");
        (await _pendingOperationStore.GetAwaitingAsync(_userId)).Should().BeEmpty();
    }
}
