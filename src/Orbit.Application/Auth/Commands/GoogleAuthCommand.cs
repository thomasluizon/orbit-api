using System.Net.Http.Headers;
using System.Text.Json;
using MediatR;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Auth.Commands;

public record GoogleAuthCommand(string AccessToken, string Language = "en", string? GoogleAccessToken = null, string? GoogleRefreshToken = null, string? ReferralCode = null)
    : IRequest<Result<LoginResponse>>, IConcurrencyRetryable;

public class GoogleAuthCommandHandler(
    IHttpClientFactory httpClientFactory,
    GoogleSignInFlow signInFlow) : IRequestHandler<GoogleAuthCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(GoogleAuthCommand request, CancellationToken cancellationToken)
    {
        var tokenResult = await ValidateGoogleTokenAsync(request.AccessToken, cancellationToken);
        if (tokenResult.IsFailure)
            return tokenResult.PropagateError<LoginResponse>();

        var (email, name) = tokenResult.Value;
        return await signInFlow.CompleteAsync(email, name, request.Language, request.ReferralCode,
            request.GoogleAccessToken, request.GoogleRefreshToken, cancellationToken);
    }

    private async Task<Result<(string Email, string Name)>> ValidateGoogleTokenAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("Supabase");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/v1/user");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Result.Failure<(string, string)>(ErrorMessages.InvalidGoogleToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
        if (string.IsNullOrEmpty(email))
            return Result.Failure<(string, string)>(ErrorMessages.GoogleEmailUnavailable);

        var name = ExtractNameFromMetadata(root);

        return Result.Success((email, name));
    }

    private static string ExtractNameFromMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("user_metadata", out var metadata))
            return "User";

        if (metadata.TryGetProperty("full_name", out var fullName) && fullName.GetString() is string fn)
            return fn;

        if (metadata.TryGetProperty("name", out var nameProperty) && nameProperty.GetString() is string n)
            return n;

        return "User";
    }

}
