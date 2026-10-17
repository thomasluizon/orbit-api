using MediatR;
using Orbit.Application.Proactive.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using System.Text.Json;

namespace Orbit.Application.Proactive.Queries;

public sealed record ProactiveItemResponse(
    Guid Id, string Trigger, DateOnly LocalDate, string Text, string ActionLabel, string ActionPrompt,
    DateTime CreatedAtUtc, DateTime? ShownAtUtc, DateTime? PushedAtUtc, ProactiveFacts? Facts);

public sealed record GetCurrentProactiveQuery(Guid UserId) : IRequest<Result<ProactiveItemResponse?>>;

public sealed class GetCurrentProactiveQueryHandler(IProactiveEngine engine)
    : IRequestHandler<GetCurrentProactiveQuery, Result<ProactiveItemResponse?>>
{
    public async Task<Result<ProactiveItemResponse?>> Handle(GetCurrentProactiveQuery request, CancellationToken cancellationToken)
    {
        var item = await engine.GetCurrentAsync(request.UserId, cancellationToken);
        return Result.Success(item is null ? null : new ProactiveItemResponse(
            item.Id, item.Trigger.ToString(), item.LocalDate, item.Text, item.ActionLabel, item.ActionPrompt,
            item.CreatedAtUtc, item.ShownAtUtc, item.PushedAtUtc,
            JsonSerializer.Deserialize<ProactiveFacts>(item.FactsJson)));
    }
}
