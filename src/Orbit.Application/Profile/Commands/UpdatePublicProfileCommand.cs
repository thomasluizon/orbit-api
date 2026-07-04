using System.Security.Cryptography;
using MediatR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

/// <summary>
/// The user's public-profile sharing settings: whether it is live, the opaque slug and composed
/// share URL (both null when disabled), and the four per-field visibility flags.
/// </summary>
public record PublicProfileSettings(
    bool Enabled,
    string? Slug,
    string? ShareUrl,
    bool ShowStreak,
    bool ShowLevel,
    bool ShowAchievements,
    bool ShowTopHabits);

public record UpdatePublicProfileCommand(
    Guid UserId,
    bool Enabled,
    bool ShowStreak,
    bool ShowLevel,
    bool ShowAchievements,
    bool ShowTopHabits,
    bool Regenerate) : IRequest<Result<PublicProfileSettings>>, IConcurrencyRetryable;

public class UpdatePublicProfileCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IDistributedCache cache,
    IOptions<FrontendSettings> frontendSettings) : IRequestHandler<UpdatePublicProfileCommand, Result<PublicProfileSettings>>
{
    private const string AllowedChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int SlugLength = 22;
    public const string CacheKeyPrefix = "public-profile:";

    public async Task<Result<PublicProfileSettings>> Handle(UpdatePublicProfileCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure<PublicProfileSettings>(ErrorMessages.UserNotFound);

        var previousSlug = user.PublicProfileSlug;

        if (!request.Enabled)
        {
            user.SetPublicProfileSlug(null);
        }
        else if (previousSlug is null || request.Regenerate)
        {
            user.SetPublicProfileSlug(await GenerateUniqueSlugAsync(cancellationToken));
        }

        user.SetPublicProfileVisibility(request.ShowStreak, request.ShowLevel, request.ShowAchievements, request.ShowTopHabits);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (previousSlug is not null)
            await cache.RemoveAsync(CacheKeyPrefix + previousSlug, cancellationToken);

        var slug = user.PublicProfileSlug;
        var shareUrl = slug is null ? null : $"{frontendSettings.Value.BaseUrl}/u/{slug}";

        return Result.Success(new PublicProfileSettings(
            slug is not null,
            slug,
            shareUrl,
            user.PublicProfileShowStreak,
            user.PublicProfileShowLevel,
            user.PublicProfileShowAchievements,
            user.PublicProfileShowTopHabits));
    }

    private async Task<string> GenerateUniqueSlugAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var slug = GenerateSlug();
            var taken = await userRepository.AnyAsync(u => u.PublicProfileSlug == slug, cancellationToken);
            if (!taken)
                return slug;
        }
    }

    private static string GenerateSlug()
    {
        return string.Create(SlugLength, AllowedChars, static (span, chars) =>
        {
            Span<byte> bytes = stackalloc byte[span.Length];
            RandomNumberGenerator.Fill(bytes);
            for (var i = 0; i < span.Length; i++)
                span[i] = chars[bytes[i] % chars.Length];
        });
    }
}
