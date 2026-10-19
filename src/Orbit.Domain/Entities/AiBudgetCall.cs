using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class AiBudgetCall : Entity
{
    public Guid UserId { get; private set; }
    public DateOnly LocalDate { get; private set; }
    public long ChargedTokens { get; private set; }
    public bool IsSettled { get; private set; }

    private AiBudgetCall() { }

    public static AiBudgetCall Reserve(Guid userId, DateOnly localDate, long reservedTokens)
    {
        if (userId == Guid.Empty || reservedTokens <= 0)
            throw new ArgumentException("A user and a positive token reservation are required.");
        return new AiBudgetCall { UserId = userId, LocalDate = localDate, ChargedTokens = reservedTokens };
    }

    public void Settle(long tokens)
    {
        if (tokens < 0)
            throw new ArgumentOutOfRangeException(nameof(tokens));
        if (IsSettled)
            return;
        ChargedTokens = tokens;
        IsSettled = true;
    }
}
