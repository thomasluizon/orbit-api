using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class SubscriptionTools(IGenericRepository<User> userRepository, IPayGateService payGate, IUserDateService userDateService, IMediator mediator, IOptions<FrontendSettings> frontendSettings, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "get_subscription_status"), Description("Get the user's subscription status, plan, trial info, and AI message usage.")]
    public async Task<CallToolResult> GetSubscriptionStatus(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var u = await userRepository.GetByIdAsync(userId, cancellationToken);
            if (u is null)
                return McpToolHelpers.Error($"Error: {ErrorMessages.UserNotFound.Message}");
            var aiLimit = await payGate.GetAiMessageLimit(userId, cancellationToken);
            var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
            var period = AiAllowancePeriod.ActiveFor(today, u.HasProAccess, u.WeekStartDay, u.AiMessagesWeekStart);
            return McpToolHelpers.Text($"Plan: {(u.HasProAccess ? "Pro" : "Free")}\n" + (u.IsTrialActive ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Trial active, ends: {u.TrialEndsAt:yyyy-MM-dd}\n") : "") + (u.PlanExpiresAt is not null ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Plan expires: {u.PlanExpiresAt:yyyy-MM-dd}\n") : "") + $"AI Messages ({period.Name}): {u.GetAiMessagesUsedForQuota(today)}/{aiLimit}\n" + $"AI Messages reset: {AiAllowanceReset.GetResetsAtUtc(u, today):O}\n" + (u.IsLifetimePro ? "Lifetime Pro: Yes\n" : "") + (u.SubscriptionInterval is not null ? $"Billing: {u.SubscriptionInterval.ToString()!.ToLowerInvariant()}" : ""));
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "get_referral_stats"), Description("Get the user's referral statistics: code, link, successful/pending referrals, and rewards.")]
    public async Task<CallToolResult> GetReferralStats(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var result = await mediator.Send(new GetReferralStatsQuery(userId), cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var s = result.Value;
            return McpToolHelpers.Text((s.ReferralCode is not null ? $"Referral Code: {s.ReferralCode}\n" : "") + (s.ReferralLink is not null ? $"Referral Link: {s.ReferralLink}\n" : "") + $"Successful Referrals: {s.SuccessfulReferrals}\n" + $"Pending Referrals: {s.PendingReferrals}\n" + $"Max Referrals: {s.MaxReferrals}\n" + (s.RewardType == "pro_days" ? $"Reward: {s.ProDaysPerReferral} days of Pro ({s.RewardType})" : $"Reward: {s.DiscountPercent}% discount ({s.RewardType})"));
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "get_referral_code"), Description("Get or create the user's referral code and shareable link.")]
    public async Task<CallToolResult> GetReferralCode(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "get_referral_code", new { }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Referral Code: {result.TargetName}\nLink: {frontendSettings.Value.BaseUrl}/r/{result.TargetName}") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "manage_subscription"), Description("Manage the user's subscription: create a checkout session or open the billing portal.")]
    public async Task<CallToolResult> ManageSubscription(
        ClaimsPrincipal user,
        [Description("Action to perform: create_checkout or create_portal")] string action,
        [Description("For create_checkout: billing interval (monthly or yearly)")] string? interval = null,
        [Description("Confirmation token from confirm_agent_operation_v2, after step_up_agent_operation_v2 and verify_step_up_agent_operation_v2 (required: managing a subscription is high-risk and needs step-up)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "manage_subscription", new { action, interval }, confirmationToken, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Subscription: {result.TargetName}") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }
}
