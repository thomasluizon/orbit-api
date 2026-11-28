using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Commands;

public sealed record CloseChatBlockCommand(Guid UserId, Guid BlockId) : IRequest<Result>;

public sealed class CloseChatBlockCommandHandler(IIssuedAiBlockStore store, IUnitOfWork unitOfWork)
    : IRequestHandler<CloseChatBlockCommand, Result>
{
    public Task<Result> Handle(CloseChatBlockCommand request, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
            if (await store.CloseAsync(request.BlockId, request.UserId, TimeProvider.System.GetUtcNow().UtcDateTime, token))
                return Result.Success();
            var block = await store.GetAsync(request.BlockId, request.UserId, token);
            if (block is null)
                return Result.Failure(ErrorMessages.AiBlockNotFound);
            return block.AnsweredAtUtc.HasValue ? Result.Failure(ErrorMessages.AiBlockAnswered) : Result.Success();
        }, cancellationToken);
}
