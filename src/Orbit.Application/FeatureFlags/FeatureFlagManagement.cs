using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Application.FeatureFlags.Commands;

namespace Orbit.Application.FeatureFlags;

public record AdminFeatureFlag(string Key, string? Description, bool EnabledForEveryone, string? PlanRequirement, IReadOnlyList<string> Accounts);

public interface IFeatureFlagManagementService
{
    Task<Result<IReadOnlyList<AdminFeatureFlag>>> ListAsync(Guid adminId, CancellationToken cancellationToken);
    Task<Result<AdminFeatureFlag>> ManageAsync(ManageFeatureFlagsCommand request, CancellationToken cancellationToken);
}

public static class FeatureFlagActions
{
    public const string List = "list";
    public const string EnableForAccounts = "enable_for_accounts";
    public const string DisableForAccounts = "disable_for_accounts";
    public const string EnableForEveryone = "enable_for_everyone";
    public const string DisableForEveryone = "disable_for_everyone";
    public const string Create = "create";
    public const string ToolName = "manage_feature_flags";
    public const string CapabilityId = "feature-flags.manage";
    public const string Scope = AgentScopes.ManageFeatureFlags;
    public const string NotFound = "FEATURE_FLAG_NOT_FOUND";
    public const string AlreadyExists = "FEATURE_FLAG_ALREADY_EXISTS";
    public const string UnknownAccounts = "FEATURE_FLAG_UNKNOWN_ACCOUNTS";

    public static readonly IReadOnlyList<string> Writes =
        [EnableForAccounts, DisableForAccounts, EnableForEveryone, DisableForEveryone, Create];
}
