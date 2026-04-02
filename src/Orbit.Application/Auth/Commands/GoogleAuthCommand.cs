using System.Net.Http.Headers;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record GoogleAuthCommand(string AccessToken, string Language = "en", string? GoogleAccessToken = null, string? GoogleRefreshToken = null, string? ReferralCode = null)
    : IRequest<Result<LoginResponse>>;

public class GoogleAuthCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    ITokenService tokenService,
    IHttpClientFactory httpClientFactory,
    IEmailService emailService,
    IMediator mediator,
    ILogger<GoogleAuthCommandHandler> logger) : IRequestHandler<GoogleAuthCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(GoogleAuthCommand request, CancellationToken cancellationToken)
    {
        // Validate token with Supabase
        var client = httpClientFactory.CreateClient("Supabase");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/v1/user");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.AccessToken);

        var response = await client.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Result.Failure<LoginResponse>("Invalid or expired Google sign-in token");

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
        if (string.IsNullOrEmpty(email))
            return Result.Failure<LoginResponse>("Could not retrieve email from Google account");

        // Extract name from user_metadata
        var name = "User";
        if (root.TryGetProperty("user_metadata", out var metadata))
        {
            if (metadata.TryGetProperty("full_name", out var fullName) && fullName.GetString() is string fn)
                name = fn;
            else if (metadata.TryGetProperty("name", out var nameProperty) && nameProperty.GetString() is string n)
                name = n;
        }

        // Find or create user (tracked so token updates persist)
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Email == email,
            cancellationToken: cancellationToken);

        var isNewUser = user is null;

        if (user is null)
        {
            var createResult = User.Create(name, email);
            if (createResult.IsFailure)
                return Result.Failure<LoginResponse>(createResult.Error);

            user = createResult.Value;
            user.SetLanguage(request.Language);
            await userRepository.AddAsync(user, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            // Send welcome email (fire and forget - don't fail auth if email fails)
            _ = Task.Run(async () =>
            {
                try
                {
                    await emailService.SendWelcomeEmailAsync(user.Email, user.Name, request.Language, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Welcome email failed for user {Email}", user.Email);
                }
            }, CancellationToken.None);
        }

        // Process referral code for new users (fire and forget -- don't fail login)
        if (isNewUser && !string.IsNullOrWhiteSpace(request.ReferralCode))
        {
            try
            {
                await mediator.Send(new ProcessReferralCodeCommand(user.Id, request.ReferralCode), cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Referral processing failed for user {UserId}", user.Id);
            }
        }

        // Cancel deactivation if user was deactivated
        var wasReactivated = false;
        if (user.IsDeactivated)
        {
            user.CancelDeactivation();
            wasReactivated = true;
        }

        // Store Google tokens for Calendar API access
        if (request.GoogleAccessToken is not null)
        {
            user.SetGoogleTokens(request.GoogleAccessToken, request.GoogleRefreshToken);
        }

        if (wasReactivated || request.GoogleAccessToken is not null)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        // Generate app JWT
        var token = tokenService.GenerateToken(user.Id, user.Email);

        return Result.Success(new LoginResponse(user.Id, token, user.Name, user.Email, wasReactivated));
    }
}
