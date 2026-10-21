using System.ClientModel.Primitives;

namespace Orbit.Infrastructure.AI;

internal sealed class AiBudgetDispatchPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        message.CancellationToken.ThrowIfCancellationRequested();
        var context = AiCallContext.Current;
        context?.MarkDispatched();
        ProcessNext(message, pipeline, currentIndex);
        if (IsExplicitRejection(message))
            context?.MarkRejected();
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        message.CancellationToken.ThrowIfCancellationRequested();
        var context = AiCallContext.Current;
        context?.MarkDispatched();
        await ProcessNextAsync(message, pipeline, currentIndex);
        if (IsExplicitRejection(message))
            context?.MarkRejected();
    }

    private static bool IsExplicitRejection(PipelineMessage message)
        => message.Response?.Status is 400 or 401 or 403 or 404 or 413 or 415 or 422 or 429 or 503;
}
