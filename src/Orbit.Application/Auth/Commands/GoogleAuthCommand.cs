using System.Net.Http.Headers;
using System.Text.Json;
using MediatR;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record GoogleAuthCommand(string AccessToken, string Language = "en")
    : IRequest<Result<LoginResponse>>;

public class GoogleAuthCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    ITokenService tokenService,
    IHttpClientFactory httpClientFactory,
    IEmailService emailService) : IRequestHandler<GoogleAuthCommand, Result<LoginResponse>>
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

        var email = root.GetProperty("email").GetString();
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

        // Find or create user
        var users = await userRepository.GetAllAsync(cancellationToken);
        var user = users.FirstOrDefault(u =>
            u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            var createResult = User.CreateFromOAuth(name, email);
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
                catch
                {
                    // Silently ignore email failures - don't block authentication
                }
            }, CancellationToken.None);
        }

        // Generate app JWT
        var token = tokenService.GenerateToken(user.Id, user.Email);

        return Result.Success(new LoginResponse(user.Id, token, user.Name, user.Email));
    }
}
