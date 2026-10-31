using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Configuration;

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
        var account = await AiEvalAccount.ResolveAsync(userId, flags,
            scope.ServiceProvider.GetService<IConfiguration>(), cancellationToken);
        var user = await db.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
        var localDate = await dates.GetUserTodayAsync(userId, cancellationToken);
        var pro = user.HasProAccess;
        var period = AiAllowancePeriod.ActiveFor(localDate, pro, user.WeekStartDay, user.AiMessagesWeekStart);
        var callLimit = await config.GetAsync(pro ? AppConfigKeys.ProAiCallsPerWeek : AppConfigKeys.FreeAiCallsPerDay,
            pro ? AppConfigKeys.DefaultProAiCallsPerWeek : AppConfigKeys.DefaultFreeAiCallsPerDay, cancellationToken);
        var spendLimit = await config.GetAsync(pro ? AppConfigKeys.ProAiMicroDollarsPerWeek : AppConfigKeys.FreeAiMicroDollarsPerDay,
            pro ? AppConfigKeys.DefaultProAiMicroDollarsPerWeek : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay, cancellationToken);
        return await work.ExecuteInTransactionAsync(async token =>
        {
            DateOnly? evalDateUtc = null;
            if (account.IsEvaluation)
            {
                var clock = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
                evalDateUtc = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                await work.AcquireAdvisoryLockAsync($"ai-eval-budget:{evalDateUtc:yyyy-MM-dd}", token);
                var settings = scope.ServiceProvider.GetService<IOptions<AiSettings>>()?.Value ?? new AiSettings();
                var ceiling = decimal.Floor(settings.EvalDailyBudgetUsd * 1_000_000m);
                var spent = await db.AiBudgetCalls.Where(call => call.EvalDateUtc == evalDateUtc)
                    .SumAsync(call => call.ChargedMicroDollars, token);
                var settled = await db.AiBudgetCalls.Where(call => call.EvalDateUtc == evalDateUtc && call.IsSettled)
                    .SumAsync(call => call.ChargedMicroDollars, token);
                var reported = await db.AiUsageDaily.Where(row => row.Date == evalDateUtc && row.Purpose.StartsWith("eval_"))
                    .Select(row => row.CostUsd).ToListAsync(token);
                spent += Math.Max(0, checked((long)decimal.Ceiling(reported.Sum() * 1_000_000m)) - settled);
                if (reservedMicroDollars > ceiling - spent)
                    throw new AiBudgetExceededException();
            }
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
            if (!account.IsUnmetered && ((totals?.Calls ?? 0) >= callLimit || reservedMicroDollars > spendLimit - (totals?.MicroDollars ?? 0)))
                throw new AiBudgetExceededException();
            var call = AiBudgetCall.Reserve(userId, localDate, reservedMicroDollars, evalDateUtc);
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
