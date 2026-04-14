using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

#pragma warning disable CA1873

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public partial class AuthController(IMediator mediator, IAgentAuditService auditService, ILogger<AuthController> logger) : ControllerBase
{
    public record SendCodeRequest(string Email, string Language = "en");
    public record VerifyCodeRequest(string Email, string Code, string Language = "en", string? ReferralCode = null);
    public record GoogleAuthRequest(string AccessToken, string Language = "en", string? GoogleAccessToken = null, string? GoogleRefreshToken = null, string? ReferralCode = null);
    public record ConfirmDeletionRequest(string Code);
    public record RefreshSessionRequest(string RefreshToken);
    public record LogoutSessionRequest(string RefreshToken);
    public record SendCodeOperationRequest(string Email, string Language = "en");
    public record VerifyCodeOperationRequest(string Email, string Code, string Language = "en", string? ReferralCode = null);
    public record GoogleAuthOperationRequest(string AccessToken, string Language = "en", string? GoogleAccessToken = null, string? GoogleRefreshToken = null, string? ReferralCode = null);
    public record RefreshSessionOperationRequest(string RefreshToken);
    public record LogoutSessionOperationRequest(string RefreshToken);

    [HttpPost("send-code")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendCode(
        [FromBody] SendCodeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SendCodeCommand(request.Email, request.Language);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogVerificationCodeSent(logger, request.Email);
            return Ok(new { message = "Verification code sent" });
        }

        LogFailedToSendCode(logger, request.Email, result.Error);
        return BadRequest(new { error = result.Error });
    }

    [HttpPost("operations/send-code")]
    [DistributedRateLimit("auth")]
    [AllowAnonymous]
    public async Task<IActionResult> SendCodeOperation(
        [FromBody] SendCodeOperationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SendCodeCommand(request.Email, request.Language), cancellationToken);
        await RecordDirectAuthAuditAsync(
            "send_auth_code",
            Guid.Empty,
            request.Email,
            result.IsSuccess ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed,
            null,
            result.IsSuccess ? null : result.Error,
            cancellationToken);

        return result.IsSuccess
            ? Ok(BuildOperationResponse(
                "send_auth_code",
                AgentOperationStatus.Succeeded,
                payload: new { success = true }))
            : BadRequest(BuildOperationResponse(
                "send_auth_code",
                AgentOperationStatus.Failed,
                policyReason: result.Error));
    }

    [HttpPost("verify-code")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyCode(
        [FromBody] VerifyCodeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new VerifyCodeCommand(request.Email, request.Code, request.Language, request.ReferralCode);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogUserLoggedInViaCode(logger, request.Email);
            return Ok(result.Value);
        }

        LogCodeVerificationFailed(logger, request.Email, result.Error);
        return Unauthorized(new { error = result.Error });
    }

    [HttpPost("operations/verify-code")]
    [DistributedRateLimit("auth")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyCodeOperation(
        [FromBody] VerifyCodeOperationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new VerifyCodeCommand(request.Email, request.Code, request.Language, request.ReferralCode),
            cancellationToken);

        await RecordDirectAuthAuditAsync(
            "verify_auth_code",
            result.IsSuccess ? result.Value.UserId : Guid.Empty,
            request.Email,
            result.IsSuccess ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed,
            result.IsSuccess ? result.Value.UserId.ToString() : null,
            result.IsSuccess ? null : result.Error,
            cancellationToken);

        return result.IsSuccess
            ? Ok(BuildOperationResponse(
                "verify_auth_code",
                AgentOperationStatus.Succeeded,
                targetId: result.Value.UserId.ToString(),
                payload: new
                {
                    access_token = result.Value.Token,
                    refresh_token = result.Value.RefreshToken,
                    user_id = result.Value.UserId,
                    name = result.Value.Name,
                    email = result.Value.Email,
                    was_reactivated = result.Value.WasReactivated
                }))
            : Unauthorized(BuildOperationResponse(
                "verify_auth_code",
                AgentOperationStatus.Failed,
                policyReason: result.Error));
    }

    [HttpPost("google")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GoogleAuth(
        [FromBody] GoogleAuthRequest request,
        CancellationToken cancellationToken)
    {
        var command = new GoogleAuthCommand(request.AccessToken, request.Language, request.GoogleAccessToken, request.GoogleRefreshToken, request.ReferralCode);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogUserLoggedInViaGoogle(logger);
            return Ok(result.Value);
        }

        LogGoogleAuthFailed(logger, result.Error);
        return Unauthorized(new { error = result.Error });
    }

    [HttpPost("operations/google")]
    [DistributedRateLimit("auth")]
    [AllowAnonymous]
    public async Task<IActionResult> GoogleAuthOperation(
        [FromBody] GoogleAuthOperationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new GoogleAuthCommand(
                request.AccessToken,
                request.Language,
                request.GoogleAccessToken,
                request.GoogleRefreshToken,
                request.ReferralCode),
            cancellationToken);

        await RecordDirectAuthAuditAsync(
            "exchange_google_auth",
            result.IsSuccess ? result.Value.UserId : Guid.Empty,
            "google_oauth",
            result.IsSuccess ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed,
            result.IsSuccess ? result.Value.UserId.ToString() : null,
            result.IsSuccess ? null : result.Error,
            cancellationToken);

        return result.IsSuccess
            ? Ok(BuildOperationResponse(
                "exchange_google_auth",
                AgentOperationStatus.Succeeded,
                targetId: result.Value.UserId.ToString(),
                payload: new
                {
                    access_token = result.Value.Token,
                    refresh_token = result.Value.RefreshToken,
                    user_id = result.Value.UserId,
                    name = result.Value.Name,
                    email = result.Value.Email,
                    was_reactivated = result.Value.WasReactivated
                }))
            : Unauthorized(BuildOperationResponse(
                "exchange_google_auth",
                AgentOperationStatus.Failed,
                policyReason: result.Error));
    }

    [HttpPost("refresh")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshSessionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new RefreshSessionCommand(request.RefreshToken);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogSessionRefreshed(logger);
            return Ok(result.Value);
        }

        LogSessionRefreshFailed(logger, result.Error);
        return Unauthorized(new { error = result.Error });
    }

    [HttpPost("operations/refresh")]
    [DistributedRateLimit("auth")]
    [AllowAnonymous]
    public async Task<IActionResult> RefreshOperation(
        [FromBody] RefreshSessionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RefreshSessionCommand(request.RefreshToken), cancellationToken);
        await RecordDirectAuthAuditAsync(
            "refresh_auth_session",
            Guid.Empty,
            "refresh_token",
            result.IsSuccess ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed,
            null,
            result.IsSuccess ? null : result.Error,
            cancellationToken);

        return result.IsSuccess
            ? Ok(BuildOperationResponse(
                "refresh_auth_session",
                AgentOperationStatus.Succeeded,
                payload: new
                {
                    access_token = result.Value.Token,
                    refresh_token = result.Value.RefreshToken
                }))
            : Unauthorized(BuildOperationResponse(
                "refresh_auth_session",
                AgentOperationStatus.Failed,
                policyReason: result.Error));
    }

    [HttpPost("logout")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout(
        [FromBody] LogoutSessionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new LogoutSessionCommand(request.RefreshToken);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogSessionRevoked(logger);
            return Ok(new { message = "Logged out" });
        }

        LogSessionRevocationFailed(logger, result.Error);
        return Unauthorized(new { error = result.Error });
    }

    [HttpPost("operations/logout")]
    [DistributedRateLimit("auth")]
    [AllowAnonymous]
    public async Task<IActionResult> LogoutOperation(
        [FromBody] LogoutSessionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new LogoutSessionCommand(request.RefreshToken), cancellationToken);
        await RecordDirectAuthAuditAsync(
            "logout_auth_session",
            Guid.Empty,
            "refresh_token",
            result.IsSuccess ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed,
            null,
            result.IsSuccess ? null : result.Error,
            cancellationToken);

        return result.IsSuccess
            ? Ok(BuildOperationResponse(
                "logout_auth_session",
                AgentOperationStatus.Succeeded,
                payload: new { success = true }))
            : Unauthorized(BuildOperationResponse(
                "logout_auth_session",
                AgentOperationStatus.Failed,
                policyReason: result.Error));
    }

    [Authorize]
    [HttpPost("request-deletion")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RequestDeletion(CancellationToken cancellationToken)
    {
        var command = new RequestAccountDeletionCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogAccountDeletionRequested(logger, HttpContext.GetUserId());
            return Ok(new { message = "Deletion code sent" });
        }

        LogDeletionRequestFailed(logger, HttpContext.GetUserId(), result.Error);
        return BadRequest(new { error = result.Error });
    }

    [Authorize]
    [HttpPost("confirm-deletion")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ConfirmDeletion(
        [FromBody] ConfirmDeletionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ConfirmAccountDeletionCommand(HttpContext.GetUserId(), request.Code);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogAccountDeactivated(logger, HttpContext.GetUserId(), result.Value);
            return Ok(new { message = "Account deactivated", scheduledDeletionAt = result.Value });
        }

        LogDeletionConfirmationFailed(logger, HttpContext.GetUserId(), result.Error);
        return BadRequest(new { error = result.Error });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Verification code sent to {Email}")]
    private static partial void LogVerificationCodeSent(ILogger logger, string email);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to send code to {Email}: {Error}")]
    private static partial void LogFailedToSendCode(ILogger logger, string email, string? error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "User logged in via code {Email}")]
    private static partial void LogUserLoggedInViaCode(ILogger logger, string email);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Code verification failed for {Email}: {Error}")]
    private static partial void LogCodeVerificationFailed(ILogger logger, string email, string? error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Google auth failed: {Error}")]
    private static partial void LogGoogleAuthFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Account deletion requested by {UserId}")]
    private static partial void LogAccountDeletionRequested(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Deletion request failed for {UserId}: {Error}")]
    private static partial void LogDeletionRequestFailed(ILogger logger, Guid userId, string? error);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Account deactivated for {UserId}, scheduled deletion at {ScheduledAt}")]
    private static partial void LogAccountDeactivated(ILogger logger, Guid userId, DateTime scheduledAt);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Deletion confirmation failed for {UserId}: {Error}")]
    private static partial void LogDeletionConfirmationFailed(ILogger logger, Guid userId, string? error);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "User logged in via Google")]
    private static partial void LogUserLoggedInViaGoogle(ILogger logger);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Session refreshed")]
    private static partial void LogSessionRefreshed(ILogger logger);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Session refresh failed: {Error}")]
    private static partial void LogSessionRefreshFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "Session revoked")]
    private static partial void LogSessionRevoked(ILogger logger);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "Session revocation failed: {Error}")]
    private static partial void LogSessionRevocationFailed(ILogger logger, string? error);

    private static AgentExecuteOperationResponse BuildOperationResponse(
        string operationId,
        AgentOperationStatus status,
        string? targetId = null,
        string? targetName = null,
        string? policyReason = null,
        object? payload = null)
    {
        return new AgentExecuteOperationResponse(
            new AgentOperationResult(
                operationId,
                operationId,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                status,
                Summary: $"{operationId} completed via direct auth flow",
                TargetId: targetId,
                TargetName: targetName,
                PolicyReason: policyReason,
                Payload: payload));
    }

    private async Task RecordDirectAuthAuditAsync(
        string operationId,
        Guid auditUserId,
        string summary,
        AgentOperationStatus outcomeStatus,
        string? targetId,
        string? error,
        CancellationToken cancellationToken)
    {
        await auditService.RecordAsync(new AgentAuditEntry(
            auditUserId,
            AgentCapabilityIds.AuthManage,
            operationId,
            AgentExecutionSurface.Metadata,
            AgentAuthMethod.Unknown,
            AgentRiskClass.Low,
            outcomeStatus == AgentOperationStatus.Succeeded ? AgentPolicyDecisionStatus.Allowed : AgentPolicyDecisionStatus.Denied,
            outcomeStatus,
            HttpContext.TraceIdentifier,
            $"Direct auth flow: {summary}",
            TargetId: targetId,
            Error: error), cancellationToken);
    }

}
