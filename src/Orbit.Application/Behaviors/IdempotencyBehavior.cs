using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Behaviors;

/// <summary>
/// Makes replays of opt-in <see cref="IIdempotentCommand"/> mutations that carry an <c>Idempotency-Key</c>
/// header exactly-once: a replayed request (a retry after a lost network ACK, routine on mobile) returns the
/// stored response instead of re-executing the handler. The reservation row is flushed first — isolating a
/// ledger unique-violation from the handler's own constraints — then commits in one transaction with the
/// handler's mutation, so a crash cannot leave the mutation applied without its idempotency record. A
/// concurrent duplicate loses the unique-index race and replays the winner's response. The ledger key is
/// scoped by request type so one key reused across two commands can't cross wires. See
/// thomasluizon/orbit-ui-mobile#243.
/// </summary>
public sealed class IdempotencyBehavior<TRequest, TResponse>(
    IIdempotencyContext idempotencyContext,
    IIdempotencyStore idempotencyStore,
    IUnitOfWork unitOfWork) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new ResultJsonConverterFactory() } };

    private static readonly string RequestType = typeof(TRequest).FullName ?? typeof(TRequest).Name;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IIdempotentCommand
            || !idempotencyContext.TryGetRequestKey(out var userId, out var idempotencyKey))
            return await next(cancellationToken);

        var storedResponse = await idempotencyStore.FindResponseBodyAsync(userId, idempotencyKey, RequestType, cancellationToken);
        if (storedResponse is not null)
            return Deserialize(storedResponse);

        var response = default(TResponse)!;
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
            {
                var reservation = idempotencyStore.Reserve(userId, idempotencyKey, RequestType);
                await unitOfWork.SaveChangesAsync(transactionToken);
                response = await next(transactionToken);
                reservation.SetResponseBody(Serialize(response));
                await unitOfWork.SaveChangesAsync(transactionToken);
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            var racedResponse = await idempotencyStore.FindResponseBodyAsync(userId, idempotencyKey, RequestType, cancellationToken);
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
