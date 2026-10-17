using System.Linq.Expressions;
using Orbit.Domain.Entities;

namespace Orbit.Application.ApiKeys.Services;

public static class ActiveApiKeyPredicate
{
    public static Expression<Func<ApiKey, bool>> ForUser(Guid userId, DateTime nowAtUtc) =>
        key => key.UserId == userId && !key.IsRevoked &&
            (key.ExpiresAtUtc == null || key.ExpiresAtUtc > nowAtUtc);
}
