namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private ModerationContextDrain? _moderationContextDrain;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Moderation_DelayedContextDrain_DoesNotCancelRequest(bool owningTransaction)
    {
        var drain = new ModerationContextDrain();
        _moderationContextDrain = drain;
        var scenario = owningTransaction
            ? Moderation_ContextCancellation_PreservesOwningTransaction(false, "self-harm")
            : Moderation_OverlapsContextAndGatesModel(true, false, true);

        try
        {
            await drain.Started;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            drain.Release();
        }

        await scenario;
    }

    /// <summary>
    /// Holds context cancellation beyond the former test deadline to reproduce cleanup cancellation:
    /// https://github.com/thomasluizon/orbit-tickets/issues/1784
    /// </summary>
    private sealed class ModerationContextDrain
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task HoldAsync()
        {
            _started.TrySetResult();
            await _release.Task;
        }

        public void Release() => _release.TrySetResult();
    }
}
