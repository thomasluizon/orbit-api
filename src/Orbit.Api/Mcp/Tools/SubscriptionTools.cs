using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Referrals.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class SubscriptionTools(
    IGenericRepository<User> userRepository,
    IPayGateService payGate,
    IMediator mediator,
    IOptions<FrontendSettings> frontendSettings)
{
    [McpServerTool(Name = "get_subscription_status"), Description("Get the user's subscription status, plan, trial info, and AI message usage.")]
    public async Task<string> GetSubscriptionStatus(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var u = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (u is null)
            return $"Error: {ErrorMessages.UserNotFound}";

        var aiLimit = await payGate.GetAiMessageLimit(userId, cancellationToken);

        return $"Plan: {(u.HasProAccess ? "Pro" : "Free")}\n" +
               (u.IsTrialActive ? $"Trial active, ends: {u.TrialEndsAt:yyyy-MM-dd}\n" : "") +
               (u.PlanExpiresAt is not null ? $"Plan expires: {u.PlanExpiresAt:yyyy-MM-dd}\n" : "") +
               $"AI Messages: {u.AiMessagesUsedThisMonth}/{aiLimit}\n" +
               (u.IsLifetimePro ? "Lifetime Pro: Yes\n" : "") +
               (u.SubscriptionInterval is not null ? $"Billing: {u.SubscriptionInterval.ToString()!.ToLowerInvariant()}" : "");
    }

    [McpServerTool(Name = "get_referral_stats"), Description("Get the user's referral statistics: code, link, successful/pending referrals, and rewards.")]
    public async Task<string> GetReferralStats(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var result = await mediator.Send(new GetReferralStatsQuery(userId), cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var s = result.Value;
        return (s.ReferralCode is not null ? $"Referral Code: {s.ReferralCode}\n" : "") +
               (s.ReferralLink is not null ? $"Referral Link: {s.ReferralLink}\n" : "") +
               $"Successful Referrals: {s.SuccessfulReferrals}\n" +
               $"Pending Referrals: {s.PendingReferrals}\n" +
               $"Max Referrals: {s.MaxReferrals}\n" +
               $"Reward: {s.DiscountPercent}% discount ({s.RewardType})";
    }

    [McpServerTool(Name = "get_referral_code"), Description("Get or create the user's referral code and shareable link.")]
    public async Task<string> GetReferralCode(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var result = await mediator.Send(new GetOrCreateReferralCodeCommand(userId), cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        return $"Referral Code: {result.Value}\nLink: {frontendSettings.Value.BaseUrl}/r/{result.Value}";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        if (!Guid.TryParse(claim, out var userId))
            throw new UnauthorizedAccessException("User ID claim is not a valid GUID");
        return userId;
    }
}
