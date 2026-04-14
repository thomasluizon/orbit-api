using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Referrals.Queries;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Application.Support.Commands;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetGamificationOverviewTool(IMediator mediator) : IAiTool
{
    public string Name => "get_gamification_overview";
    public string Description => "Read the user's gamification profile, achievements, and streak information.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            include_profile = new { type = JsonSchemaTypes.Boolean },
            include_achievements = new { type = JsonSchemaTypes.Boolean },
            include_streak = new { type = JsonSchemaTypes.Boolean }
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var includeProfile = JsonArgumentParser.GetOptionalBool(args, "include_profile") ?? true;
        var includeAchievements = JsonArgumentParser.GetOptionalBool(args, "include_achievements") ?? true;
        var includeStreak = JsonArgumentParser.GetOptionalBool(args, "include_streak") ?? true;

        object? profile = null;
        object? achievements = null;
        object? streak = null;

        if (includeProfile)
        {
            var profileResult = await mediator.Send(new GetGamificationProfileQuery(userId), ct);
            if (profileResult.IsFailure) return new ToolResult(false, Error: profileResult.Error);
            profile = profileResult.Value;
        }

        if (includeAchievements)
        {
            var achievementsResult = await mediator.Send(new GetAchievementsQuery(userId), ct);
            if (achievementsResult.IsFailure) return new ToolResult(false, Error: achievementsResult.Error);
            achievements = achievementsResult.Value;
        }

        if (includeStreak)
        {
            var streakResult = await mediator.Send(new GetStreakInfoQuery(userId), ct);
            if (streakResult.IsFailure) return new ToolResult(false, Error: streakResult.Error);
            streak = streakResult.Value;
        }

        return new ToolResult(true, Payload: new { profile, achievements, streak });
    }
}

public class ActivateStreakFreezeTool(IMediator mediator) : IAiTool
{
    public string Name => "activate_streak_freeze";
    public string Description => "Activate one available streak freeze for the user.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new ActivateStreakFreezeCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Activated streak freeze", Payload: result.Value)
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }
}

public class GetReferralOverviewTool(IMediator mediator) : IAiTool
{
    public string Name => "get_referral_overview";
    public string Description => "Read the user's referral dashboard, code, link, and stats.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetReferralDashboardQuery(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : new ToolResult(false, Error: result.Error);
    }
}

public class GetSubscriptionOverviewTool(IMediator mediator) : IAiTool
{
    public string Name => "get_subscription_overview";
    public string Description => "Read subscription status, billing details, and available plans.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            include_status = new { type = JsonSchemaTypes.Boolean },
            include_billing = new { type = JsonSchemaTypes.Boolean },
            include_plans = new { type = JsonSchemaTypes.Boolean }
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var includeStatus = JsonArgumentParser.GetOptionalBool(args, "include_status") ?? true;
        var includeBilling = JsonArgumentParser.GetOptionalBool(args, "include_billing") ?? true;
        var includePlans = JsonArgumentParser.GetOptionalBool(args, "include_plans") ?? true;

        object? status = null;
        object? billing = null;
        object? plans = null;

        if (includeStatus)
        {
            var statusResult = await mediator.Send(new GetSubscriptionStatusQuery(userId), ct);
            if (statusResult.IsFailure) return new ToolResult(false, Error: statusResult.Error);
            status = statusResult.Value;
        }

        if (includeBilling)
        {
            var billingResult = await mediator.Send(new GetBillingDetailsQuery(userId), ct);
            if (billingResult.IsFailure) return new ToolResult(false, Error: billingResult.Error);
            billing = billingResult.Value;
        }

        if (includePlans)
        {
            var plansResult = await mediator.Send(new GetPlansQuery(userId, null, null), ct);
            if (plansResult.IsFailure) return new ToolResult(false, Error: plansResult.Error);
            plans = plansResult.Value;
        }

        return new ToolResult(true, Payload: new { status, billing, plans });
    }
}

public class ManageSubscriptionTool(IMediator mediator) : IAiTool
{
    public string Name => "manage_subscription";
    public string Description => "Create a checkout session, create a billing portal session, or claim an ad reward.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new { type = JsonSchemaTypes.String, @enum = new[] { "create_checkout", "create_portal", "claim_ad_reward" } },
            interval = new { type = JsonSchemaTypes.String, nullable = true, @enum = new[] { "monthly", "yearly" } }
        },
        required = new[] { "action" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "create_checkout" => await CreateCheckoutAsync(args, userId, ct),
            "create_portal" => await CreatePortalAsync(userId, ct),
            "claim_ad_reward" => await ClaimAdRewardAsync(userId, ct),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> CreateCheckoutAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var interval = JsonArgumentParser.GetOptionalString(args, "interval");
        if (string.IsNullOrWhiteSpace(interval))
            return new ToolResult(false, Error: "interval is required.");

        var result = await mediator.Send(new CreateCheckoutCommand(userId, interval, null, null), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Created checkout session", Payload: result.Value)
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }

    private async Task<ToolResult> CreatePortalAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new CreatePortalSessionCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Created billing portal session", Payload: result.Value)
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }

    private async Task<ToolResult> ClaimAdRewardAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new ClaimAdRewardCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Claimed ad reward", Payload: result.Value)
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }
}

public class GetApiKeysTool(IMediator mediator) : IAiTool
{
    public string Name => "get_api_keys";
    public string Description => "Read the user's API keys, scopes, last use, and revocation state.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetApiKeysQuery(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : new ToolResult(false, Error: result.Error);
    }
}

public class ManageApiKeysTool(IMediator mediator) : IAiTool
{
    public string Name => "manage_api_keys";
    public string Description => "Create or revoke scoped API keys.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new { type = JsonSchemaTypes.String, @enum = new[] { "create", "revoke" } },
            key_id = new { type = JsonSchemaTypes.String, nullable = true },
            name = new { type = JsonSchemaTypes.String, nullable = true },
            scopes = new
            {
                type = JsonSchemaTypes.Array,
                nullable = true,
                items = new { type = JsonSchemaTypes.String }
            },
            is_read_only = new { type = JsonSchemaTypes.Boolean, nullable = true },
            expires_at_utc = new { type = JsonSchemaTypes.String, nullable = true, description = "ISO-8601 UTC timestamp." }
        },
        required = new[] { "action" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "create" => await CreateAsync(args, userId, ct),
            "revoke" => await RevokeAsync(args, userId, ct),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> CreateAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var name = JsonArgumentParser.GetOptionalString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return new ToolResult(false, Error: "name is required.");

        var scopes = JsonArgumentParser.ParseStringArray(args, "scopes");
        var isReadOnly = JsonArgumentParser.GetOptionalBool(args, "is_read_only") ?? false;
        var expiresAtUtc = ParseUtcTimestamp(JsonArgumentParser.GetOptionalString(args, "expires_at_utc"));

        var result = await mediator.Send(new CreateApiKeyCommand(userId, name, scopes, isReadOnly, expiresAtUtc), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: result.Value.Id.ToString(), EntityName: result.Value.Name, Payload: result.Value)
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }

    private async Task<ToolResult> RevokeAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var keyId = JsonArgumentParser.GetOptionalString(args, "key_id");
        if (!Guid.TryParse(keyId, out var parsedId))
            return new ToolResult(false, Error: "key_id must be a valid GUID.");

        var result = await mediator.Send(new RevokeApiKeyCommand(userId, parsedId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: parsedId.ToString(), EntityName: "Revoked API key", Payload: new { id = parsedId })
            : new ToolResult(false, EntityId: parsedId.ToString(), Error: result.Error);
    }

    private static DateTime? ParseUtcTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}

public class SendSupportRequestTool(IMediator mediator) : IAiTool
{
    public string Name => "send_support_request";
    public string Description => "Send a support request on behalf of the user.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            name = new { type = JsonSchemaTypes.String },
            email = new { type = JsonSchemaTypes.String },
            subject = new { type = JsonSchemaTypes.String },
            message = new { type = JsonSchemaTypes.String }
        },
        required = new[] { "name", "email", "subject", "message" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var name = JsonArgumentParser.GetOptionalString(args, "name");
        var email = JsonArgumentParser.GetOptionalString(args, "email");
        var subject = JsonArgumentParser.GetOptionalString(args, "subject");
        var message = JsonArgumentParser.GetOptionalString(args, "message");

        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(subject) ||
            string.IsNullOrWhiteSpace(message))
        {
            return new ToolResult(false, Error: "name, email, subject, and message are required.");
        }

        var result = await mediator.Send(new SendSupportCommand(userId, name, email, subject, message), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Support request sent", Payload: new { subject })
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }
}

public class ManageAccountTool(IMediator mediator) : IAiTool
{
    public string Name => "manage_account";
    public string Description => "Reset the account, request an account deletion code, or confirm account deletion with a code.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new { type = JsonSchemaTypes.String, @enum = new[] { "reset_account", "request_deletion", "confirm_deletion" } },
            code = new { type = JsonSchemaTypes.String, nullable = true }
        },
        required = new[] { "action" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "reset_account" => await ResetAccountAsync(userId, ct),
            "request_deletion" => await RequestDeletionAsync(userId, ct),
            "confirm_deletion" => await ConfirmDeletionAsync(args, userId, ct),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> ResetAccountAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new ResetAccountCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Account reset completed", Payload: new { success = true })
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }

    private async Task<ToolResult> RequestDeletionAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new RequestAccountDeletionCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Deletion code requested", Payload: new { success = true })
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }

    private async Task<ToolResult> ConfirmDeletionAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var code = JsonArgumentParser.GetOptionalString(args, "code");
        if (string.IsNullOrWhiteSpace(code))
            return new ToolResult(false, Error: "code is required.");

        var result = await mediator.Send(new ConfirmAccountDeletionCommand(userId, code), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Account deletion confirmed", Payload: new { scheduledDeletionAt = result.Value })
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }
}
