using Orbit.Domain.Common;

namespace Orbit.Application.Common;

public static class ResultExtensions
{
    public static Result<T> PropagateError<T>(this Result source)
    {
        if (source.ErrorCode == Result.PayGateErrorCode)
            return Result.PayGateFailure<T>(source.Error);

        return source.ErrorCode is not null
            ? Result.Failure<T>(source.Error, source.ErrorCode)
            : Result.Failure<T>(source.Error);
    }

    public static Result PropagateError(this Result source)
    {
        if (source.ErrorCode == Result.PayGateErrorCode)
            return Result.PayGateFailure(source.Error);

        return source.ErrorCode is not null
            ? Result.Failure(source.Error, source.ErrorCode)
            : Result.Failure(source.Error);
    }
}
