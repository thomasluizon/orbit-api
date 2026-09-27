using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public interface IGoogleIdTokenValidator
{
    Task<Result<(string Email, string Name)>> ValidateAsync(string idToken);
}

public sealed class GoogleIdTokenValidator(IOptions<GoogleSettings> options) : IGoogleIdTokenValidator
{
    public async Task<Result<(string Email, string Name)>> ValidateAsync(string idToken)
    {
        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = [options.Value.ClientId!],
                    ExpirationTimeClockTolerance = TimeSpan.Zero
                });
            return ValidateClaims(payload, options.Value.ClientId!, TimeProvider.System.GetUtcNow().ToUnixTimeSeconds());
        }
        catch (InvalidJwtException)
        {
            return Result.Failure<(string, string)>(ErrorMessages.InvalidGoogleToken);
        }
    }

    internal static Result<(string Email, string Name)> ValidateClaims(
        GoogleJsonWebSignature.Payload payload, string clientId, long nowUnixSeconds)
    {
        if (payload.Issuer is not ("accounts.google.com" or "https://accounts.google.com")
            || payload.Audience is not string audience
            || !string.Equals(audience, clientId, StringComparison.Ordinal)
            || payload.ExpirationTimeSeconds is null or <= 0
            || payload.ExpirationTimeSeconds <= nowUnixSeconds
            || !payload.EmailVerified)
            return Result.Failure<(string, string)>(ErrorMessages.InvalidGoogleToken);
        if (string.IsNullOrWhiteSpace(payload.Email))
            return Result.Failure<(string, string)>(ErrorMessages.GoogleEmailUnavailable);

        return Result.Success((payload.Email, string.IsNullOrWhiteSpace(payload.Name) ? "User" : payload.Name));
    }
}

public sealed class GoogleAuthorizationCodeService(
    IHttpClientFactory httpClientFactory,
    IOptions<GoogleSettings> options,
    IGoogleIdTokenValidator idTokenValidator) : IGoogleAuthorizationCodeService
{
    public const string HttpClientName = "GoogleAuthorizationCode";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    public async Task<Result<GoogleCodeIdentity>> ExchangeAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.AllowedRedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            return Result.Failure<GoogleCodeIdentity>(ErrorMessages.GoogleRedirectUriNotAllowed);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = settings.ClientId!,
                    ["client_secret"] = settings.ClientSecret!,
                    ["code"] = code,
                    ["code_verifier"] = codeVerifier,
                    ["redirect_uri"] = redirectUri,
                    ["grant_type"] = "authorization_code"
                })
            };
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Result.Failure<GoogleCodeIdentity>(ErrorMessages.GoogleCodeExchangeFailed);

            var tokens = await TokenResponse.FromHttpResponseAsync(
                response, SystemClock.Default, Google.ApplicationContext.Logger);
            var idToken = tokens.IdToken;
            if (string.IsNullOrWhiteSpace(idToken))
                return Result.Failure<GoogleCodeIdentity>(ErrorMessages.InvalidGoogleToken);

            var identity = await idTokenValidator.ValidateAsync(idToken);
            if (identity.IsFailure)
                return identity.PropagateError<GoogleCodeIdentity>();

            return Result.Success(new GoogleCodeIdentity(
                identity.Value.Email, identity.Value.Name,
                NonEmpty(tokens.AccessToken), NonEmpty(tokens.RefreshToken)));
        }
        catch (InvalidJwtException)
        {
            return Result.Failure<GoogleCodeIdentity>(ErrorMessages.InvalidGoogleToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TokenResponseException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<GoogleCodeIdentity>(ErrorMessages.GoogleCodeExchangeFailed);
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
