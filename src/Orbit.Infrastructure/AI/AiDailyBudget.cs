using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.AI;

public sealed class AiDailyBudget(IServiceScopeFactory scopes) : IAiDailyBudget
{
    /// <summary>
    /// Legacy rows lack the input/output split; $10 per million is the highest configured legacy rate.
    /// The original token column remains readable during rolling deployments.
    /// https://developers.openai.com/api/docs/models/gpt-6.1-sol
    /// </summary>
    private const long LegacyMaxMicroDollarsPerToken = 10;

    public async Task<Guid> ReserveAsync(Guid userId, long reservedMicroDollars, CancellationToken cancellationToken)
    {
        if (reservedMicroDollars <= 0)
            throw new ArgumentOutOfRangeException(nameof(reservedMicroDollars));
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var config = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
        var dates = scope.ServiceProvider.GetRequiredService<IUserDateService>();
        var flags = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();
        var unmetered = (await flags.GetEnabledKeysForUserAsync(userId, cancellationToken))
            .Contains(FeatureFlagKeys.AstraEvalUnmetered, StringComparer.Ordinal);
        var user = await db.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
        var localDate = await dates.GetUserTodayAsync(userId, cancellationToken);
        var pro = user.HasProAccess;
        var period = AiAllowancePeriod.For(localDate, pro, user.WeekStartDay);
        var callLimit = await config.GetAsync(pro ? AppConfigKeys.ProAiCallsPerWeek : AppConfigKeys.FreeAiCallsPerDay,
            pro ? AppConfigKeys.DefaultProAiCallsPerWeek : AppConfigKeys.DefaultFreeAiCallsPerDay, cancellationToken);
        var spendLimit = await config.GetAsync(pro ? AppConfigKeys.ProAiMicroDollarsPerWeek : AppConfigKeys.FreeAiMicroDollarsPerDay,
            pro ? AppConfigKeys.DefaultProAiMicroDollarsPerWeek : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay, cancellationToken);
        return await work.ExecuteInTransactionAsync(async token =>
        {
            await work.AcquireAdvisoryLockAsync($"ai-budget:{userId}:{period.Start:yyyy-MM-dd}", token);
            var query = db.AiBudgetCalls.Where(call => call.UserId == userId && call.LocalDate >= period.Start && call.LocalDate < period.End);
            var totals = await query.GroupBy(call => call.UserId)
                .Select(group => new
                {
                    Calls = group.Count(),
                    MicroDollars = group.Sum(call => call.ChargedMicroDollars == 0
                    ? EF.Property<long>(call, "ChargedTokens") * LegacyMaxMicroDollarsPerToken
                    : call.ChargedMicroDollars)
                }).SingleOrDefaultAsync(token);
            if (!unmetered && ((totals?.Calls ?? 0) >= callLimit || reservedMicroDollars > spendLimit - (totals?.MicroDollars ?? 0)))
                throw new AiBudgetExceededException();
            var call = AiBudgetCall.Reserve(userId, localDate, reservedMicroDollars);
            db.AiBudgetCalls.Add(call);
            await work.SaveChangesAsync(token);
            return call.Id;
        }, cancellationToken);
    }

    public async Task SettleAsync(Guid callId, long microDollars, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var call = await db.AiBudgetCalls.SingleAsync(call => call.Id == callId, cancellationToken);
        call.Settle(microDollars);
        await db.SaveChangesAsync(cancellationToken);
    }
}
