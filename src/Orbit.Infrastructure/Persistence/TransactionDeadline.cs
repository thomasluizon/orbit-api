using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Persistence;

internal sealed class TransactionDeadline : IDisposable
{
    private readonly OrbitDbContext _context;
    private readonly CancellationToken? _previousCompletionToken;
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _operation;

    public TransactionDeadline(OrbitDbContext context, DatabaseConnectionSettings settings, CancellationToken caller)
    {
        _context = context;
        _previousCompletionToken = context.TransactionCompletionToken;
        _timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TransactionTimeoutSeconds));
        _operation = CancellationTokenSource.CreateLinkedTokenSource(caller, _timeout.Token);
        context.TransactionCompletionToken = _timeout.Token;
    }

    public CancellationToken OperationToken => _operation.Token;
    public CancellationToken CompletionToken => _timeout.Token;
    public bool IsExpired => _timeout.IsCancellationRequested;

    public void Dispose()
    {
        _context.TransactionCompletionToken = _previousCompletionToken;
        _operation.Dispose();
        _timeout.Dispose();
    }
}
