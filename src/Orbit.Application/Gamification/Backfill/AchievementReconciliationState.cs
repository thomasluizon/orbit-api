namespace Orbit.Application.Gamification.Backfill;

public interface IAchievementReconciliationState
{
    bool IsComplete { get; }

    void MarkComplete();
}

public sealed class AchievementReconciliationState : IAchievementReconciliationState
{
    private int _isComplete;

    public bool IsComplete => Volatile.Read(ref _isComplete) == 1;

    public void MarkComplete()
    {
        Interlocked.Exchange(ref _isComplete, 1);
    }
}
