using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.AI;

public sealed class AiDailyBudget(IServiceScopeFactory scopes) : IAiDailyBudget
{
    public async Task<Guid> ReserveAsync(Guid userId, long reservedTokens, CancellationToken cancellationToken)
    {
        if (reservedTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(reservedTokens));
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var config = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
        var dates = scope.ServiceProvider.GetRequiredService<IUserDateService>();
        var user = await db.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
        var localDate = await dates.GetUserTodayAsync(userId, cancellationToken);
        var pro = user.HasProAccess;
        var callLimit = await config.GetAsync(pro ? AppConfigKeys.ProAiCallsPerDay : AppConfigKeys.FreeAiCallsPerDay,
            pro ? AppConfigKeys.DefaultProAiCallsPerDay : AppConfigKeys.DefaultFreeAiCallsPerDay, cancellationToken);
        var tokenLimit = await config.GetAsync(pro ? AppConfigKeys.ProAiTokensPerDay : AppConfigKeys.FreeAiTokensPerDay,
            pro ? AppConfigKeys.DefaultProAiTokensPerDay : AppConfigKeys.DefaultFreeAiTokensPerDay, cancellationToken);
        tokenLimit = Math.Min(tokenLimit, pro ? AppConfigKeys.DefaultProAiTokensPerDay : AppConfigKeys.DefaultFreeAiTokensPerDay);
        return await work.ExecuteInTransactionAsync(async token =>
        {
            await work.AcquireAdvisoryLockAsync($"ai-budget:{userId}:{localDate:yyyy-MM-dd}", token);
            var query = db.AiBudgetCalls.Where(call => call.UserId == userId && call.LocalDate == localDate);
            var totals = await query.GroupBy(call => call.UserId)
                .Select(group => new { Calls = group.Count(), Tokens = group.Sum(call => call.ChargedTokens) }).SingleOrDefaultAsync(token);
            if ((totals?.Calls ?? 0) >= callLimit || reservedTokens > tokenLimit - (totals?.Tokens ?? 0))
                throw new AiBudgetExceededException();
            var call = AiBudgetCall.Reserve(userId, localDate, reservedTokens);
            db.AiBudgetCalls.Add(call);
            await work.SaveChangesAsync(token);
            return call.Id;
        }, cancellationToken);
    }

    public async Task SettleAsync(Guid callId, long? tokens, CancellationToken cancellationToken)
    {
        if (tokens is null)
            return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var call = await db.AiBudgetCalls.SingleAsync(call => call.Id == callId, cancellationToken);
        call.Settle(tokens.Value);
        await db.SaveChangesAsync(cancellationToken);
    }
}
