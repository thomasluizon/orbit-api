using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Connections;

public sealed record AuthorizeNotionCommand(Guid UserId, string RedirectUri) : IRequest<Result<NotionAuthorizationResponse>>;
public sealed record CompleteNotionCommand(Guid UserId, string Code, string State, string RedirectUri) : IRequest<Result>;
public sealed record GetNotionConnectionQuery(Guid UserId) : IRequest<Result<NotionConnectionResponse>>;
public sealed record DisconnectNotionCommand(Guid UserId) : IRequest<Result>;
public sealed record GetNotionTasksQuery(Guid UserId) : IRequest<Result<IReadOnlyList<NotionTaskResponse>>>;

public sealed class AuthorizeNotionCommandHandler(NotionAccessGate gate, INotionConnectionService service)
    : IRequestHandler<AuthorizeNotionCommand, Result<NotionAuthorizationResponse>>
{
    public async Task<Result<NotionAuthorizationResponse>> Handle(AuthorizeNotionCommand request, CancellationToken ct)
    {
        var access = await gate.CheckAsync(request.UserId, ct);
        return access.IsFailure ? access.PropagateError<NotionAuthorizationResponse>()
            : await service.AuthorizeAsync(request.UserId, request.RedirectUri, ct);
    }
}

public sealed class CompleteNotionCommandHandler(NotionAccessGate gate, INotionConnectionService service)
    : IRequestHandler<CompleteNotionCommand, Result>
{
    public async Task<Result> Handle(CompleteNotionCommand request, CancellationToken ct)
    {
        var access = await gate.CheckAsync(request.UserId, ct);
        return access.IsFailure ? access : await service.CompleteAsync(request.UserId, request.Code, request.State, request.RedirectUri, ct);
    }
}

public sealed class GetNotionConnectionQueryHandler(NotionAccessGate gate, INotionConnectionService service)
    : IRequestHandler<GetNotionConnectionQuery, Result<NotionConnectionResponse>>
{
    public async Task<Result<NotionConnectionResponse>> Handle(GetNotionConnectionQuery request, CancellationToken ct)
    {
        var access = await gate.CheckAsync(request.UserId, ct);
        return access.IsFailure ? access.PropagateError<NotionConnectionResponse>()
            : Result.Success(await service.GetAsync(request.UserId, ct));
    }
}

public sealed class DisconnectNotionCommandHandler(NotionAccessGate gate, INotionConnectionService service)
    : IRequestHandler<DisconnectNotionCommand, Result>
{
    public async Task<Result> Handle(DisconnectNotionCommand request, CancellationToken ct)
    {
        var access = await gate.CheckAsync(request.UserId, ct);
        return access.IsFailure ? access : await service.DisconnectAsync(request.UserId, ct);
    }
}

public sealed class GetNotionTasksQueryHandler(NotionAccessGate gate, IGenericRepository<NotionTask> tasks)
    : IRequestHandler<GetNotionTasksQuery, Result<IReadOnlyList<NotionTaskResponse>>>
{
    public async Task<Result<IReadOnlyList<NotionTaskResponse>>> Handle(GetNotionTasksQuery request, CancellationToken ct)
    {
        var access = await gate.CheckAsync(request.UserId, ct);
        if (access.IsFailure) return access.PropagateError<IReadOnlyList<NotionTaskResponse>>();
        var stored = await tasks.FindAsync(t => t.UserId == request.UserId, ct);
        return Result.Success<IReadOnlyList<NotionTaskResponse>>(stored.OrderBy(t => t.DueDate)
            .ThenBy(t => t.PageId).Take(100).Select(NotionTaskResponse.FromEntity).ToArray());
    }
}
