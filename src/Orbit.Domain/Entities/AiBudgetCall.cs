using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class AiBudgetCall : Entity
{
    public Guid UserId { get; private set; }
    public DateOnly LocalDate { get; private set; }
    public long ChargedMicroDollars { get; private set; }
    public bool IsSettled { get; private set; }

    private AiBudgetCall() { }

    public static AiBudgetCall Reserve(Guid userId, DateOnly localDate, long reservedMicroDollars)
    {
        if (userId == Guid.Empty || reservedMicroDollars <= 0)
            throw new ArgumentException("A user and a positive spend reservation are required.");
        return new AiBudgetCall { UserId = userId, LocalDate = localDate, ChargedMicroDollars = reservedMicroDollars };
    }

    public void Settle(long microDollars)
    {
        if (microDollars < 0)
            throw new ArgumentOutOfRangeException(nameof(microDollars));
        if (IsSettled)
            return;
        ChargedMicroDollars = microDollars;
        IsSettled = true;
    }
}
