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
        var requestOrdinal = idempotencyContext.NextRequestOrdinal(RequestType);
        if (request is IIdempotencyFingerprint fingerprint)
            requestType = $"{RequestType}:{fingerprint.IdempotencyFingerprint}";
        var key = new LedgerKey(userId, idempotencyKey, requestType, requestOrdinal);
        var storedResponse = await FindStoredResponseAsync(key, cancellationToken);
        if (storedResponse is not null)
            return Deserialize(storedResponse);

        try
        {
            return request is IProviderCallCommand { CallsProvider: true }
                ? await HandleProviderCallAsync(next, key, cancellationToken)
                : await HandleInTransactionAsync(next, key, cancellationToken);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            var racedResponse = await FindStoredResponseAsync(key, cancellationToken);
            if (racedResponse is null)
                throw;

            return Deserialize(racedResponse);
        }
    }

    private async Task<TResponse> HandleInTransactionAsync(
        RequestHandlerDelegate<TResponse> next, LedgerKey key, CancellationToken cancellationToken)
    {
        var response = default(TResponse)!;
        await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            var reservation = Reserve(key);
            await unitOfWork.SaveChangesAsync(transactionToken);
            response = await next(transactionToken);
            reservation.SetResponseBody(Serialize(response));
            await unitOfWork.SaveChangesAsync(transactionToken);
        }, cancellationToken);
        return response;
    }

    /// <summary>
    /// Commits the reservation alone, runs the handler with no open transaction, then stores the response.
    /// A failed handler releases the key. The store and the release ignore the request cancellation, because
    /// the handler has an outcome at that point and the reservation must agree with it.
    /// </summary>
    private async Task<TResponse> HandleProviderCallAsync(
        RequestHandlerDelegate<TResponse> next, LedgerKey key, CancellationToken cancellationToken)
    {
        var reservation = await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            var reserved = Reserve(key);
            await unitOfWork.SaveChangesAsync(transactionToken);
            return reserved;
        }, cancellationToken);

        TResponse response;
        try
        {
            response = await next(cancellationToken);
        }
        catch
        {
            await reservation.ReleaseAsync(CancellationToken.None);
            throw;
        }

        await reservation.StoreResponseBodyAsync(Serialize(response), CancellationToken.None);
        return response;
    }

    private IIdempotencyReservation Reserve(LedgerKey key) =>
        idempotencyStore.Reserve(key.UserId, key.IdempotencyKey, key.RequestType, key.RequestOrdinal);

    private async Task<string?> FindStoredResponseAsync(LedgerKey key, CancellationToken cancellationToken)
    {
        var responseBody = await idempotencyStore.FindResponseBodyAsync(
            key.UserId, key.IdempotencyKey, key.RequestType, key.RequestOrdinal, cancellationToken);
        return string.IsNullOrEmpty(responseBody) ? null : responseBody;
    }

    private static string Serialize(TResponse response) =>
        JsonSerializer.Serialize(response, SerializerOptions);

    private static TResponse Deserialize(string responseBody) =>
        JsonSerializer.Deserialize<TResponse>(responseBody, SerializerOptions)!;

    private readonly record struct LedgerKey(Guid UserId, string IdempotencyKey, string RequestType, int RequestOrdinal);
}
