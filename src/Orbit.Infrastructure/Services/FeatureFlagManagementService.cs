using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class FeatureFlagManagementService(
    OrbitDbContext dbContext, IFeatureFlagService resolver, ILogger<FeatureFlagManagementService> logger)
    : IFeatureFlagManagementService
{
    public async Task<Result<IReadOnlyList<AdminFeatureFlag>>> ListAsync(Guid adminId, CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(adminId, cancellationToken))
            return Result.Failure<IReadOnlyList<AdminFeatureFlag>>(error: "Unknown tool: 'manage_feature_flags'", errorCode: ErrorCodes.NoPermission);

        return Result.Success<IReadOnlyList<AdminFeatureFlag>>(await ReadFlags().ToListAsync(cancellationToken));
    }

    public async Task<Result<AdminFeatureFlag>> ManageAsync(ManageFeatureFlagsCommand request, CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(request.AdminId, cancellationToken))
            return Result.Failure<AdminFeatureFlag>(error: "Unknown tool: 'manage_feature_flags'", errorCode: ErrorCodes.NoPermission);

        var flag = await dbContext.AppFeatureFlags.SingleOrDefaultAsync(flag => flag.Key == request.Key, cancellationToken);
        var accounts = new List<User>();
        if (request.Action is FeatureFlagActions.EnableForAccounts or FeatureFlagActions.DisableForAccounts)
        {
            var emails = (request.Emails ?? []).Select(email => email.Trim().ToLowerInvariant()).Distinct().ToList();
            accounts = await dbContext.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(user => emails.Contains(user.Email.ToLower())).ToListAsync(cancellationToken);
            var found = accounts.Select(user => user.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = emails.Where(email => !found.Contains(email)).ToList();
            if (unknown.Count > 0)
                return Result.Failure<AdminFeatureFlag>(error: $"Unknown account emails: {string.Join(", ", unknown)}", errorCode: FeatureFlagActions.UnknownAccounts);
        }

        if (request.Action == FeatureFlagActions.Create)
        {
            if (flag is not null)
                return Result.Failure<AdminFeatureFlag>(error: "Feature flag already exists.", errorCode: FeatureFlagActions.AlreadyExists);
            flag = AppFeatureFlag.Create(request.Key, false, description: request.Description);
            dbContext.AppFeatureFlags.Add(flag);
        }
        else
        {
            if (flag is null)
                return Result.Failure<AdminFeatureFlag>(error: "Feature flag not found.", errorCode: FeatureFlagActions.NotFound);

            switch (request.Action)
            {
                case FeatureFlagActions.EnableForEveryone:
                case FeatureFlagActions.DisableForEveryone:
                    flag.SetEnabled(request.Action == FeatureFlagActions.EnableForEveryone);
                    break;
                case FeatureFlagActions.EnableForAccounts:
                case FeatureFlagActions.DisableForAccounts:
                    var userIds = accounts.Select(account => account.Id).ToList();
                    var targets = await dbContext.AppFeatureFlagAccounts
                        .Where(target => target.FlagKey == request.Key && userIds.Contains(target.UserId))
                        .ToListAsync(cancellationToken);
                    if (request.Action == FeatureFlagActions.DisableForAccounts)
                        dbContext.AppFeatureFlagAccounts.RemoveRange(targets);
                    else
                    {
                        var existing = targets.Select(target => target.UserId).ToHashSet();
                        dbContext.AppFeatureFlagAccounts.AddRange(userIds.Where(id => !existing.Contains(id))
                            .Select(id => AppFeatureFlagAccount.Create(request.Key, id)));
                    }
                    break;
                default:
                    return Result.Failure<AdminFeatureFlag>(error: "Unsupported feature flag action.", errorCode: "INVALID_ACTION");
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        resolver.Invalidate();
        logger.LogInformation("Admin {AdminId} performed {Action} on feature flag {FlagKey} for accounts {Accounts}",
            request.AdminId, request.Action, request.Key, request.Action is FeatureFlagActions.EnableForEveryone or FeatureFlagActions.DisableForEveryone
                ? "everyone" : string.Join(", ", accounts.Select(account => account.Email)));
        return Result.Success(await ReadFlags(request.Key).SingleAsync(cancellationToken));
    }

    private Task<bool> IsAdminAsync(Guid adminId, CancellationToken cancellationToken) =>
        dbContext.Users.IgnoreQueryFilters().AnyAsync(user => user.Id == adminId && user.IsAdmin, cancellationToken);

    private IQueryable<AdminFeatureFlag> ReadFlags(string? key = null) => dbContext.AppFeatureFlags.AsNoTracking()
        .Where(flag => key == null || flag.Key == key).OrderBy(flag => flag.Key)
        .Select(flag => new AdminFeatureFlag(flag.Key, flag.Description, flag.Enabled, flag.PlanRequirement,
            dbContext.AppFeatureFlagAccounts.Where(target => target.FlagKey == flag.Key)
                .Join(dbContext.Users.IgnoreQueryFilters(), target => target.UserId, user => user.Id, (target, user) => user.Email)
                .OrderBy(email => email).ToList()));
}
