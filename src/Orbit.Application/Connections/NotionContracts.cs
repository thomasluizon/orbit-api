using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Application.Connections;

public sealed record NotionAuthorizationResponse(string AuthorizeUrl);
public sealed record NotionDatabase(Guid Id, string Title);
public sealed record NotionConnectionResponse(bool Connected, string? WorkspaceName, string Status,
    DateTime? LastPollAtUtc, IReadOnlyList<NotionDatabase> SharedDatabases);
public sealed record NotionTaskResponse(Guid DatabaseId, Guid PageId, string Title, DateOnly? DueDate,
    string? Status, string Url, DateTime NotionLastEditedAtUtc, DateTime FetchedAtUtc)
{
    public static NotionTaskResponse FromEntity(NotionTask task) => new(task.DatabaseId, task.PageId,
        task.Title[..Math.Min(task.Title.Length, NotionTask.MaxTitleLength)], task.DueDate, task.Status,
        task.Url, task.NotionLastEditedAtUtc, task.FetchedAtUtc);
}

public interface INotionConnectionService
{
    Task<Result<NotionAuthorizationResponse>> AuthorizeAsync(Guid userId, string redirectUri, CancellationToken ct);
    Task<Result> CompleteAsync(Guid userId, string code, string state, string redirectUri, CancellationToken ct);
    Task<NotionConnectionResponse> GetAsync(Guid userId, CancellationToken ct);
    Task<Result> DisconnectAsync(Guid userId, CancellationToken ct);
}
