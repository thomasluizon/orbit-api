using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Queries;

public sealed record RefreshReadBlockRequest(string Token, AgentClientContext? ClientContext = null, bool Page = false);
public sealed record RefreshGamificationReadBlockRequest(string Token, AgentClientContext? ClientContext = null);
public sealed record RefreshHabitMonthReadBlockRequest(string Token, AgentClientContext? ClientContext = null);
public sealed record RefreshReadBlockQuery(Guid UserId, string Token, AgentClientContext? ClientContext,
    bool Page = false, AgentAuthMethod AuthMethod = AgentAuthMethod.Jwt,
    IReadOnlyList<string>? GrantedScopes = null, bool ReadOnlyCredential = false, bool GamificationOnly = false,
    bool HabitMonthOnly = false)
    : IRequest<Result<QueryReadBlock>>;

public sealed class RefreshReadBlockQueryHandler(IReadQueryTokenService tokens, IAgentOperationExecutor executor,
    ReadBlockFactory blocks, IFeatureFlagService flags, IProductAnalytics? analytics = null, ILogger<RefreshReadBlockQueryHandler>? logger = null) : IRequestHandler<RefreshReadBlockQuery, Result<QueryReadBlock>>
{
    public async Task<Result<QueryReadBlock>> Handle(RefreshReadBlockQuery request, CancellationToken ct)
    {
        var result = await RefreshAsync(request, ct);
        if (analytics is not null)
        {
            var ticket = tokens.Read(request.Token, request.UserId);
            var read = result.IsSuccess ? result.Value.Read : new ReadBlockMetadata(Guid.Empty, ticket?.Tool ?? "unknown",
                default, string.Empty, "unknown", string.Empty);
            var metric = ticket?.Arguments.TryGetProperty("metric", out var value) == true && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            ReadBlockTelemetry.Capture(analytics, logger ?? NullLogger<RefreshReadBlockQueryHandler>.Instance, request.UserId,
                "chat_read_refresh", read, ticket?.Kind ?? "unknown", metric,
                result.IsSuccess ? "succeeded" : result.ErrorCode == ErrorMessages.NoPermission.Code ? "scopeMismatch" : "failed");
        }
        return result;
    }

    private async Task<Result<QueryReadBlock>> RefreshAsync(RefreshReadBlockQuery request, CancellationToken ct)
    {
        var ticket = tokens.Read(request.Token, request.UserId);
        if (ticket is null || !ReadBlockFactory.Supports(ticket.Tool, ProactiveProposal.Enabled(request.ClientContext)))
            return Result.Failure<QueryReadBlock>("This exact query is unavailable. Repeat the original request.", errorCode: "READ_QUERY_EXPIRED");
        if (!MatchesContract(request, ticket.Kind))
            return Result.Failure<QueryReadBlock>(ErrorMessages.NoPermission);
        if (request.GamificationOnly && request.Page)
            return Result.Failure<QueryReadBlock>("Gamification reads do not support paging.", errorCode: "READ_QUERY_PAGE_INVALID");
        var enabled = await flags.GetEnabledKeysForUserAsync(request.UserId, ct);
        if (!Orbit.Application.Chat.Commands.ProcessUserChatCommandHandler.ReadKindEnabled(ticket.Kind,
            request.ClientContext, enabled)
            || ticket.Tool == "get_habit_history" && request.ClientContext?.SupportsHabitHistoryCard != true)
            return Result.Failure<QueryReadBlock>(ErrorMessages.NoPermission);
        if (request.Page && ticket.Offset <= 0)
            return Result.Failure<QueryReadBlock>("Use the exact query's next cursor.", errorCode: "READ_QUERY_PAGE_INVALID");
        var args = JsonNode.Parse(ticket.Arguments.GetRawText())!.AsObject();
        args["offset"] = request.Page ? ticket.Offset : 0;
        if (ticket.Tool is "get_daily_summary" or "get_retrospective") args["generate_narrative"] = false;
        var arguments = JsonSerializer.SerializeToElement(args);
        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(request.UserId, ticket.Tool,
            arguments, AgentExecutionSurface.Chat, request.AuthMethod, request.GrantedScopes, request.ReadOnlyCredential), ct);
        if (response.Operation.Status != AgentOperationStatus.Succeeded && response.Operation.ErrorCode is { } sourceError)
            return Result.Failure<QueryReadBlock>(response.Operation.PolicyReason ?? "The original read is unavailable.", sourceError);
        if (response.Operation.Status is AgentOperationStatus.Denied or AgentOperationStatus.UnsupportedByPolicy)
            return Result.Failure<QueryReadBlock>(ErrorMessages.NoPermission);
        if (response.Operation.Status != AgentOperationStatus.Succeeded)
            return Result.Failure<QueryReadBlock>("The exact query could not be refreshed. Retry this query.", errorCode: "READ_QUERY_REFRESH_FAILED");
        var block = blocks.Capture(ticket.Tool, arguments, request.UserId, response.Operation.Payload, ticket);
        if (block is null || block.Kind != ticket.Kind)
            return Result.Failure<QueryReadBlock>("The exact query could not be refreshed. Retry this query.", errorCode: "READ_QUERY_REFRESH_FAILED");
        if (!Orbit.Application.Chat.Commands.ProcessUserChatCommandHandler.ReadSubtypeEnabled(block.Block, request.ClientContext,
            enabled.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraBlockFirst)))
            return Result.Failure<QueryReadBlock>(ErrorMessages.NoPermission);
        if (request.Page && block.Read.Revision != ticket.Revision)
            return Result.Failure<QueryReadBlock>("The source changed. Refresh the original query before paging.", errorCode: "READ_QUERY_STALE");
        return Result.Success(block);
    }

    private static bool MatchesContract(RefreshReadBlockQuery request, string kind) =>
        (kind == "gamification") == request.GamificationOnly
        && (kind == HabitMonthCardBuilder.Kind) == request.HabitMonthOnly
        && !(request.HabitMonthOnly && request.Page);
}
