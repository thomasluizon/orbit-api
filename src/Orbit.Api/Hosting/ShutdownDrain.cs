using Microsoft.Extensions.Options;

namespace Orbit.Api.Hosting;

public sealed class ShutdownDrainOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(240);
}

public sealed partial class ShutdownDrain(
    IHostApplicationLifetime lifetime,
    IOptions<ShutdownDrainOptions> options,
    ILogger<ShutdownDrain> logger) : IHostedLifecycleService, IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _expired = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeRequests;
    private bool _stopping;

    public CancellationToken ExecutionDeadline => _expired.Token;

    public Lease? TryEnter(CancellationToken requestAborted)
    {
        lock (_gate)
        {
            if (_stopping || lifetime.ApplicationStopping.IsCancellationRequested)
                return null;

            _activeRequests++;
            return new Lease(this, CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _expired.Token));
        }
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopping = true;
            if (_activeRequests == 0)
                _drained.TrySetResult();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Value.Timeout);
        try
        {
            await _drained.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            LogDrainExpired(logger);
            await _expired.CancelAsync();
        }
    }

    private void Exit()
    {
        lock (_gate)
        {
            _activeRequests--;
            if (_stopping && _activeRequests == 0)
                _drained.TrySetResult();
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _expired.Dispose();

    public sealed class Lease(ShutdownDrain owner, CancellationTokenSource cancellation) : IDisposable
    {
        private int _disposed;
        public CancellationToken Token => cancellation.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            cancellation.Dispose();
            owner.Exit();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request shutdown drain expired; cancelling active requests")]
    private static partial void LogDrainExpired(ILogger logger);
}
