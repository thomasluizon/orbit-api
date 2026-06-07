using Orbit.Application.Common;
using Orbit.Domain.Enums;

namespace Orbit.IntegrationTests;

/// <summary>
/// Test double for <see cref="IPlayBillingService"/> that removes the live Google Play Developer
/// API dependency from billing integration tests. Tests set <see cref="NextState"/> before the
/// request and read back the recorded token/acknowledge flag. Tests run in the Sequential
/// collection, so the shared instance is mutated and read with no cross-test race.
/// </summary>
public sealed class CapturingPlayBillingService : IPlayBillingService
{
    public PlaySubscriptionState? NextState { get; set; } =
        new(true, DateTime.UtcNow.AddMonths(1), SubscriptionInterval.Monthly, false, "orbit_pro", null, null);

    public string? LastVerifiedToken { get; private set; }
    public bool AcknowledgeCalled { get; private set; }

    public Task<PlaySubscriptionState?> VerifyAsync(string productId, string purchaseToken, CancellationToken cancellationToken)
    {
        LastVerifiedToken = purchaseToken;
        return Task.FromResult(NextState);
    }

    public Task AcknowledgeAsync(string productId, string purchaseToken, CancellationToken cancellationToken)
    {
        AcknowledgeCalled = true;
        return Task.CompletedTask;
    }
}
