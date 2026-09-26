using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Behaviors;

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

        var requestType = RequestType;
        var requestOrdinal = 0;
        int? legacyOrdinal = null;
        if (request is IIdempotencyFingerprint fingerprint)
        {
            requestType = $"{RequestType}:{fingerprint.IdempotencyFingerprint}";
            legacyOrdinal = idempotencyContext.NextRequestOrdinal(RequestType);
        }
        else
            requestOrdinal = idempotencyContext.NextRequestOrdinal(RequestType);
        var storedResponse = await idempotencyStore.FindResponseBodyAsync(
            userId, idempotencyKey, requestType, requestOrdinal, cancellationToken);
        if (storedResponse is null && legacyOrdinal is { } ordinal)
            storedResponse = await idempotencyStore.FindResponseBodyAsync(
                userId, idempotencyKey, RequestType, ordinal, cancellationToken);
        if (storedResponse is not null)
            return Deserialize(storedResponse);

        var response = default(TResponse)!;
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
            {
                var reservation = idempotencyStore.Reserve(userId, idempotencyKey, requestType, requestOrdinal);
                await unitOfWork.SaveChangesAsync(transactionToken);
                response = await next(transactionToken);
                reservation.SetResponseBody(Serialize(response));
                await unitOfWork.SaveChangesAsync(transactionToken);
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            var racedResponse = await idempotencyStore.FindResponseBodyAsync(
                userId, idempotencyKey, requestType, requestOrdinal, cancellationToken);
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
