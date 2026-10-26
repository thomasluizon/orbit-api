using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Tests.Common;

namespace Orbit.Application.Tests.Chat;

public class ChatTurnTimerTests
{
    [Fact]
    public void NestedPhases_ExcludeChildrenAndRetainFullElapsedTime()
    {
        var clock = new ChatTimingClock();
        using var timer = ChatTurnTimer.Begin(clock);
        clock.Advance(2);
        using (ChatTurnTimer.Measure(ChatPhase.ToolCall))
        {
            clock.Advance(3);
            using (ChatTurnTimer.Measure(ChatPhase.Save))
                clock.Advance(5);
            clock.Advance(7);
        }
        var timing = timer.Complete();

        timing.TotalMilliseconds.Should().Be(17);
        timing.Phases.Select(phase => phase.Name).Should().Equal("tool_call", "save", "other");
        timing.Phases.Select(phase => phase.Milliseconds).Should().Equal(10, 5, 2);
        timing.Phases[0].ElapsedMilliseconds.Should().Be(15);
        timing.Phases.Sum(phase => phase.Milliseconds).Should().Be(timing.TotalMilliseconds);
        clock.Advance(100);
        timer.Complete().Should().BeSameAs(timing);
    }

    [Fact]
    public async Task ConcurrentTools_ShareOnlyOverlappingWallTime()
    {
        var clock = new ChatTimingClock();
        using var timer = ChatTurnTimer.Begin(clock);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ToolAsync(finishFirst.Task);
        var second = ToolAsync(finishSecond.Task);
        clock.Advance(10);
        finishFirst.SetResult();
        await first;
        clock.Advance(5);
        finishSecond.SetResult();
        await second;

        var timing = timer.Complete();
        timing.TotalMilliseconds.Should().Be(15);
        timing.Phases.Select(phase => phase.Milliseconds).Should().Equal(5, 10, 0);
        timing.Phases.Select(phase => phase.ElapsedMilliseconds).Should().Equal(10, 15, 0);
        timing.Phases.Sum(phase => phase.Milliseconds).Should().Be(timing.TotalMilliseconds);

        static async Task ToolAsync(Task finish)
        {
            using var phase = ChatTurnTimer.Measure(ChatPhase.ToolCall);
            await finish;
        }
    }

    [Fact]
    public async Task IndependentTurns_DoNotSharePhasesOrLeakAmbientState()
    {
        async Task<string[]> TurnAsync(ChatPhase phase)
        {
            using var timer = ChatTurnTimer.Begin(new ChatTimingClock());
            using (ChatTurnTimer.Measure(phase))
                await Task.Yield();
            return timer.Complete().Phases.Select(item => item.Name).ToArray();
        }

        var results = await Task.WhenAll(TurnAsync(ChatPhase.Moderation), TurnAsync(ChatPhase.Save));
        results[0].Should().Equal("moderation", "other");
        results[1].Should().Equal("save", "other");
        ChatTurnTimer.Measure(ChatPhase.ModelCall).Should().BeNull();
    }
}
