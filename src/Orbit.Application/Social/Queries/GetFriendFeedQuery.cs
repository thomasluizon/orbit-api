using System.Text;
using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Queries;

public record FriendFeedItem(
    Guid Id,
    Guid ActorUserId,
    string ActorHandle,
    string ActorDisplayName,
    string Type,
    int? Value,
    string? AchievementId,
    DateTime CreatedAtUtc);

public record FriendFeedPage(IReadOnlyList<FriendFeedItem> Items, string? NextCursor);

public record GetFriendFeedQuery(Guid UserId, string? Cursor, int? PageSize) : IRequest<Result<FriendFeedPage>>;

public class GetFriendFeedQueryHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    IGenericRepository<Domain.Entities.BlockedUser> blockedUserRepository,
    IGenericRepository<Domain.Entities.User> userRepository,
    IFriendFeedReader friendFeedReader) : IRequestHandler<GetFriendFeedQuery, Result<FriendFeedPage>>
{
    public async Task<Result<FriendFeedPage>> Handle(GetFriendFeedQuery request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<FriendFeedPage>();

        var actorMap = await ResolveVisibleActorsAsync(request.UserId, cancellationToken);
        if (actorMap.Count == 0)
            return Result.Success(new FriendFeedPage([], null));

        var pageSize = Math.Clamp(
            request.PageSize ?? AppConstants.FriendFeedPageSize,
            1,
            AppConstants.MaxFriendFeedPageSize);

        DateTime? cursorCreatedAtUtc = null;
        Guid? cursorId = null;
        if (!string.IsNullOrEmpty(request.Cursor) && FeedCursor.TryDecode(request.Cursor, out var time, out var id))
        {
            cursorCreatedAtUtc = time;
            cursorId = id;
        }

        var rows = await friendFeedReader.ReadFeedPageAsync(
            actorMap.Keys.ToList(),
            cursorCreatedAtUtc,
            cursorId,
            pageSize + 1,
            cancellationToken);

        var hasMore = rows.Count > pageSize;
        var pageRows = hasMore ? rows.Take(pageSize).ToList() : rows;

        var items = pageRows
            .Select(e =>
            {
                actorMap.TryGetValue(e.ActorUserId, out var actor);
                return new FriendFeedItem(
                    e.Id,
                    e.ActorUserId,
                    actor.Handle ?? string.Empty,
                    actor.DisplayName ?? string.Empty,
                    e.Type.ToString(),
                    e.Value,
                    e.AchievementId,
                    e.CreatedAtUtc);
            })
            .ToList();

        var nextCursor = hasMore && pageRows.Count > 0
            ? FeedCursor.Encode(pageRows[^1].CreatedAtUtc, pageRows[^1].Id)
            : null;

        return Result.Success(new FriendFeedPage(items, nextCursor));
    }

    private async Task<Dictionary<Guid, (string Handle, string DisplayName)>> ResolveVisibleActorsAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var friendIds = await friendGraphService.GetAcceptedFriendIdsAsync(userId, cancellationToken);
        if (friendIds.Count == 0)
            return [];

        var blocks = await blockedUserRepository.FindAsync(
            b => b.BlockerId == userId || b.BlockedId == userId,
            cancellationToken);
        var blockedIds = blocks
            .Select(b => b.BlockerId == userId ? b.BlockedId : b.BlockerId)
            .ToHashSet();

        var candidateIds = friendIds.Where(id => !blockedIds.Contains(id)).ToHashSet();
        if (candidateIds.Count == 0)
            return [];

        var users = await userRepository.FindAsync(u => candidateIds.Contains(u.Id), cancellationToken);

        return users
            .Where(u => u.SocialOptIn)
            .ToDictionary(u => u.Id, u => (u.Handle ?? string.Empty, u.Name));
    }
}

/// <summary>
/// Opaque, URL-safe keyset cursor pairing a row's CreatedAtUtc ticks with its Id, so the feed paginates
/// stably under concurrent inserts (page boundaries are anchored to a row, not an offset).
/// </summary>
internal static class FeedCursor
{
    public static string Encode(DateTime createdAtUtc, Guid id) =>
        Base64UrlEncode($"{createdAtUtc.Ticks}:{id:N}");

    public static bool TryDecode(string cursor, out DateTime createdAtUtc, out Guid id)
    {
        createdAtUtc = default;
        id = default;

        var decoded = Base64UrlDecode(cursor);
        if (decoded is null)
            return false;

        var parts = decoded.Split(':');
        if (parts.Length != 2)
            return false;

        if (!long.TryParse(parts[0], out var ticks) || ticks < 0 || ticks > DateTime.MaxValue.Ticks)
            return false;

        if (!Guid.TryParseExact(parts[1], "N", out id))
            return false;

        createdAtUtc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static string Base64UrlEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string? Base64UrlDecode(string value)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch
            {
                2 => "==",
                3 => "=",
                _ => string.Empty
            };
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
