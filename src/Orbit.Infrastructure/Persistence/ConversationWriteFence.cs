using Microsoft.EntityFrameworkCore;

namespace Orbit.Infrastructure.Persistence;

internal static class ConversationWriteFence
{
    public static T Execute<T>(OrbitDbContext db, IReadOnlyCollection<Guid> userIds, Func<T> operation)
    {
        T Locked()
        {
            foreach (var userId in userIds.Order())
                UserRowLock.Acquire(db, userId);
            return operation();
        }

        return db.Database.IsRelational() ? VerifiedTransaction.Execute(db, Locked) : Locked();
    }
}
