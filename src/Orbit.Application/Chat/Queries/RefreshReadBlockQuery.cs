using System.Text.Json.Nodes;
using System.Text.Json;
using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Queries;

public sealed record RefreshReadBlockRequest(string Token, AgentClientContext? ClientContext = null, bool Page = false);
public sealed record RefreshReadBlockQuery(Guid UserId, string Token, AgentClientContext? ClientContext,
    bool Page = false, AgentAuthMethod AuthMethod = AgentAuthMethod.Jwt,
    IReadOnlyList<string>? GrantedScopes = null, bool ReadOnlyCredential = false)
    : IRequest<Result<QueryReadBlock>>;

public sealed class RefreshReadBlockQueryHandler(IReadQueryTokenService tokens, IAgentOperationExecutor executor,
    ReadBlockFactory blocks, IFeatureFlagService flags) : IRequestHandler<RefreshReadBlockQuery, Result<QueryReadBlock>>
{
    public async Task<Result<QueryReadBlock>> Handle(RefreshReadBlockQuery request, CancellationToken ct)
    {
        var ticket = tokens.Read(request.Token, request.UserId);
        if (ticket is null || !ReadBlockFactory.Supports(ticket.Tool))
            return Result.Failure<QueryReadBlock>("This exact query is unavailable. Repeat the original request.", errorCode: "READ_QUERY_EXPIRED");
        var enabled = await flags.GetEnabledKeysForUserAsync(request.UserId, ct);
        if (!Orbit.Application.Chat.Commands.ProcessUserChatCommandHandler.ReadKindEnabled(ticket.Kind,
            request.ClientContext, enabled))
            return Result.Failure<QueryReadBlock>(ErrorMessages.NoPermission);
        if (request.Page && ticket.Offset <= 0)
            return Result.Failure<QueryReadBlock>("Use the exact query's next cursor.", errorCode: "READ_QUERY_PAGE_INVALID");
        var args = JsonNode.Parse(ticket.Arguments.GetRawText())!.AsObject();
        args["offset"] = request.Page ? ticket.Offset : 0;
        if (ticket.Tool is "get_daily_summary" or "get_retrospective") args["generate_narrative"] = false;
        var arguments = JsonSerializer.SerializeToElement(args);
        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(request.UserId, ticket.Tool,
            arguments, AgentExecutionSurface.Chat, request.AuthMethod, request.GrantedScopes, request.ReadOnlyCredential), ct);
        if (response.Operation.Status is AgentOperationStatus.Denied or AgentOperationStatus.UnsupportedByPolicy)
            return Result.Failure<QueryReadBlock>(ErrorMessages.NoPermission);
        if (response.Operation.Status != AgentOperationStatus.Succeeded)
            return Result.Failure<QueryReadBlock>("The exact query could not be refreshed. Retry this query.", errorCode: "READ_QUERY_REFRESH_FAILED");
        var block = blocks.Capture(ticket.Tool, arguments, request.UserId, response.Operation.Payload, ticket);
        if (block is null || block.Kind != ticket.Kind)
            return Result.Failure<QueryReadBlock>("The exact query could not be refreshed. Retry this query.", errorCode: "READ_QUERY_REFRESH_FAILED");
        if (request.Page && block.Read.Revision != ticket.Revision)
            return Result.Failure<QueryReadBlock>("The source changed. Refresh the original query before paging.", errorCode: "READ_QUERY_STALE");
        return Result.Success(block);
    }
}
