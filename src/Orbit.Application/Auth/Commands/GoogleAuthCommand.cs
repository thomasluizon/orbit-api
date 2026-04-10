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

public partial class GoogleAuthCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IAuthSessionService authSessionService,
    IHttpClientFactory httpClientFactory,
    IEmailService emailService,
    IMediator mediator,
    ILogger<GoogleAuthCommandHandler> logger) : IRequestHandler<GoogleAuthCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(GoogleAuthCommand request, CancellationToken cancellationToken)
    {
        var tokenResult = await ValidateGoogleTokenAsync(request.AccessToken, cancellationToken);
        if (tokenResult.IsFailure)
            return Result.Failure<LoginResponse>(tokenResult.Error);

        var (email, name) = tokenResult.Value;

        var findResult = await FindOrCreateUserAsync(email, name, request.Language, cancellationToken);
        if (findResult.IsFailure)
            return Result.Failure<LoginResponse>(findResult.Error);

        var (user, isNewUser) = findResult.Value;

        var wasReactivated = HandlePostLogin(user, request, isNewUser, cancellationToken);

        if (wasReactivated || request.GoogleAccessToken is not null)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        var sessionResult = await authSessionService.CreateSessionAsync(user.Id, user.Email, cancellationToken);
        if (sessionResult.IsFailure)
            return Result.Failure<LoginResponse>(sessionResult.Error, sessionResult.ErrorCode!);

        return Result.Success(new LoginResponse(
            user.Id,
            sessionResult.Value.AccessToken,
            user.Name,
            user.Email,
            wasReactivated,
            sessionResult.Value.RefreshToken));
    }

    private async Task<Result<(string Email, string Name)>> ValidateGoogleTokenAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("Supabase");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/v1/user");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Result.Failure<(string, string)>("Invalid or expired Google sign-in token");

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
        if (string.IsNullOrEmpty(email))
            return Result.Failure<(string, string)>("Could not retrieve email from Google account");

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

    private async Task<Result<(User User, bool IsNew)>> FindOrCreateUserAsync(
        string email, string name, string language, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Email == email,
            cancellationToken: cancellationToken);

        if (user is not null)
            return Result.Success((user, false));

        var createResult = User.Create(name, email);
        if (createResult.IsFailure)
            return Result.Failure<(User, bool)>(createResult.Error);

        user = createResult.Value;
        user.SetLanguage(language);
        await userRepository.AddAsync(user, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        SendWelcomeEmailInBackground(user.Email, user.Name, language);

        return Result.Success((user, true));
    }

    private void SendWelcomeEmailInBackground(string email, string name, string language)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await emailService.SendWelcomeEmailAsync(email, name, language, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWelcomeEmailFailed(logger, ex, email);
            }
        }, CancellationToken.None);
    }

    private bool HandlePostLogin(
        User user, GoogleAuthCommand request, bool isNewUser, CancellationToken cancellationToken)
    {
        if (isNewUser && !string.IsNullOrWhiteSpace(request.ReferralCode))
            _ = ProcessReferralSafeAsync(user.Id, request.ReferralCode, cancellationToken);

        var wasReactivated = false;
        if (user.IsDeactivated)
        {
            user.CancelDeactivation();
            wasReactivated = true;
        }

        if (request.GoogleAccessToken is not null)
            user.SetGoogleTokens(request.GoogleAccessToken, request.GoogleRefreshToken);

        return wasReactivated;
    }

    private async Task ProcessReferralSafeAsync(Guid userId, string referralCode, CancellationToken cancellationToken)
    {
        try
        {
            await mediator.Send(new ProcessReferralCodeCommand(userId, referralCode), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReferralProcessingFailed(logger, ex, userId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Welcome email failed for user {Email}")]
    private static partial void LogWelcomeEmailFailed(ILogger logger, Exception ex, string email);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Referral processing failed for user {UserId}")]
    private static partial void LogReferralProcessingFailed(ILogger logger, Exception ex, Guid userId);
}
