using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Services;

public sealed record AppleAuthorizationRequest(string ClientState, string Nonce, string Language, string? ReferralCode, string RedirectUri, string? McpContext = null);
public sealed record AppleCallbackResult(LoginResponse Login, AppleAuthorizationRequest Request);
public sealed record AppleHandoff(string State, LoginResponse Login);

public sealed class AppleAuthorizationFlow(IOAuthStateStore states, IAppleIdentityService apple, AppleSignInFlow signIn,
    IOptions<AppleSignInSettings> settings, TimeProvider time)
{
    private readonly AppleSignInSettings _settings = settings.Value;

    public string? Start(string state, string language, string? referralCode, string? mcpContext = null)
    {
        if (!apple.IsConfigured || mcpContext is not null && !_settings.IsMcpConfigured)
            return null;
        states.RemoveExpired(time.GetUtcNow().UtcDateTime);
        var secret = NewSecret();
        var nonce = NewSecret();
        var redirectUri = mcpContext is null ? _settings.RedirectUri : _settings.McpRedirectUri;
        var request = new AppleAuthorizationRequest(state, nonce, language, referralCode, redirectUri, mcpContext);
        if (!states.Put(secret, "apple-authorization", JsonSerializer.Serialize(request), time.GetUtcNow().UtcDateTime.AddMinutes(5), 1_000))
            return null;
        return "https://appleid.apple.com/auth/authorize?response_type=code&response_mode=form_post&scope=name%20email"
            + $"&client_id={Uri.EscapeDataString(_settings.WebClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&state={Uri.EscapeDataString(secret)}&nonce={Uri.EscapeDataString(nonce)}";
    }

    public async Task<Result<AppleCallbackResult>> CompleteAsync(string state, string code, string? userJson, bool mcp, CancellationToken cancellationToken)
    {
        if (!apple.IsConfigured)
            return Result.Failure<AppleCallbackResult>(ErrorMessages.InvalidSession);
        var stored = states.Take(state, "apple-authorization", time.GetUtcNow().UtcDateTime);
        var request = stored is null ? null : JsonSerializer.Deserialize<AppleAuthorizationRequest>(stored);
        if (request is null || (request.McpContext is not null) != mcp)
            return Result.Failure<AppleCallbackResult>(ErrorMessages.InvalidSession);
        try
        {
            var identity = await apple.AuthenticateWebAsync(code, request.Nonce, request.RedirectUri, cancellationToken);
            var name = ReadName(userJson);
            var login = await signIn.CompleteAsync(identity, name, request.Language, request.ReferralCode, cancellationToken);
            return login.IsSuccess ? Result.Success(new AppleCallbackResult(login.Value, request)) : login.PropagateError<AppleCallbackResult>();
        }
        catch (AppleVerificationException)
        {
            return Result.Failure<AppleCallbackResult>(ErrorMessages.InvalidSession);
        }
    }

    public async Task<Result<string>> CompleteWebCallbackAsync(string state, string code, string? userJson, CancellationToken cancellationToken)
    {
        var result = await CompleteAsync(state, code, userJson, false, cancellationToken);
        if (result.IsFailure)
            return result.PropagateError<string>();
        var handoff = IssueHandoff(result.Value);
        return handoff is null ? Result.Failure<string>(ErrorMessages.InvalidSession)
            : Result.Success($"{_settings.WebOrigin.TrimEnd('/')}/auth-callback?provider=apple&handoff={Uri.EscapeDataString(handoff)}&state={Uri.EscapeDataString(result.Value.Request.ClientState)}");
    }

    public string? IssueHandoff(AppleCallbackResult result)
    {
        var handoff = NewSecret();
        return states.Put(handoff, "apple-handoff", JsonSerializer.Serialize(new AppleHandoff(result.Request.ClientState, result.Login)),
            time.GetUtcNow().UtcDateTime.AddSeconds(60), 1_000) ? handoff : null;
    }

    public Result<LoginResponse> TakeHandoff(string handoff, string state)
    {
        if (!apple.IsConfigured)
            return Result.Failure<LoginResponse>(ErrorMessages.InvalidSession);
        var stored = states.Take(handoff, "apple-handoff", time.GetUtcNow().UtcDateTime);
        var value = stored is null ? null : JsonSerializer.Deserialize<AppleHandoff>(stored);
        return value is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value.State), Encoding.UTF8.GetBytes(state))
            ? Result.Success(value.Login) : Result.Failure<LoginResponse>(ErrorMessages.InvalidSession);
    }

    private static string? ReadName(string? userJson)
    {
        if (string.IsNullOrEmpty(userJson))
            return null;
        try
        {
            using var payload = JsonDocument.Parse(userJson);
            if (!payload.RootElement.TryGetProperty("name", out var name))
                return null;
            var parts = new[] { "firstName", "lastName" }.Select(key => name.TryGetProperty(key, out var part) && part.ValueKind == JsonValueKind.String ? part.GetString() : null);
            var value = string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
            return value.Length <= 200 && !value.Any(char.IsControl) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
