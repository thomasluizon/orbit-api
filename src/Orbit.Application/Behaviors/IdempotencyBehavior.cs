using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Behaviors;

/// <summary>
/// Makes client mutations that carry an <c>Idempotency-Key</c> header exactly-once: a replayed request
/// (a retry after a lost network ACK, routine on mobile) returns the stored response instead of
/// re-executing the handler. The reservation row and the handler's mutation commit in one transaction,
/// so a crash cannot leave the mutation applied without its idempotency record. A concurrent duplicate
/// loses the unique-index race and replays the winner's response. See thomasluizon/orbit-ui-mobile#243.
/// </summary>
public sealed class IdempotencyBehavior<TRequest, TResponse>(
    IIdempotencyContext idempotencyContext,
    IIdempotencyStore idempotencyStore,
    IUnitOfWork unitOfWork) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!idempotencyContext.TryGetRequestKey(out var userId, out var idempotencyKey))
            return await next(cancellationToken);

        var storedResponse = await idempotencyStore.FindResponseBodyAsync(userId, idempotencyKey, cancellationToken);
        if (storedResponse is not null)
            return Deserialize(storedResponse);

        var response = default(TResponse)!;
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
            {
                var reservation = idempotencyStore.Reserve(userId, idempotencyKey);
                response = await next(transactionToken);
                reservation.SetResponseBody(Serialize(response));
                await unitOfWork.SaveChangesAsync(transactionToken);
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            var racedResponse = await idempotencyStore.FindResponseBodyAsync(userId, idempotencyKey, cancellationToken);
            if (racedResponse is null)
                throw;

            return Deserialize(racedResponse);
        }

        return response;
    }

    private static string Serialize(TResponse response) =>
        JsonSerializer.Serialize(response, SerializerOptions);

    private static TResponse Deserialize(string responseBody) =>
        JsonSerializer.Deserialize<TResponse>(responseBody, SerializerOptions)!;
}
